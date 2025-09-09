
using Docker.DotNet;
using Docker.DotNet.Models;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/run", async (HttpRequest request) =>
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body);
        var code = doc.RootElement.GetProperty("code").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new { error = "empty code" });
        if (code.Length > 200_000)
            return Results.BadRequest(new { error = "too big" });

        var sdkImage = "mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview";
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST") ?? "unix:///var/run/docker.sock";
        var docker = new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

        var tmpName = Path.GetRandomFileName().Replace(".", "");
        var imageTag = $"local/dotnet10runner:{tmpName}";

        var csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                     "  <PropertyGroup>\n" +
                     "    <OutputType>Exe</OutputType>\n" +
                     "    <TargetFramework>net10.0</TargetFramework>\n" +
                     "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                     "    <Nullable>enable</Nullable>\n" +
                     "  </PropertyGroup>\n" +
                     "</Project>\n";

        var programCs = code;

        var dockerfile =
            "FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview\n" +
            "ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \\\n" +
            "    DOTNET_NoLogo=1\n" +
            "WORKDIR /work\n" +
            "COPY Runner.csproj ./\n" +
            "RUN dotnet restore --source https://api.nuget.org/v3/index.json --disable-parallel\n" +
            "COPY Program.cs ./\n" +
            "RUN dotnet build -c Release -o /out --no-restore\n" +
            "CMD [\"dotnet\", \"/out/Runner.dll\"]\n";

        // Build gzip'd tar context
        using var buildContext = new MemoryStream();
        using (var gz = new GZipStream(buildContext, CompressionLevel.Fastest, leaveOpen: true))
        {
            using var tw = new TarWriter(gz, leaveOpen: true);
            tw.AddFile("Program.cs", Encoding.UTF8.GetBytes(programCs));
            tw.AddFile("Runner.csproj", Encoding.UTF8.GetBytes(csproj));
            tw.AddFile("Dockerfile", Encoding.UTF8.GetBytes(dockerfile));
        }
        buildContext.Position = 0;

        // Pull base (ignore if already present)
        try { await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = sdkImage }, null, new Progress<JSONMessage>()); } catch {}

        // Build and wait using progress overload (use named args to avoid signature confusion)
        var buildLogs = new StringBuilder();
        var buildHadError = false;
        var progress = new Progress<JSONMessage>(m =>
        {
            if (m == null) return;
            if (!string.IsNullOrEmpty(m.Stream)) buildLogs.Append(m.Stream);
            if (!string.IsNullOrEmpty(m.Status)) buildLogs.AppendLine(m.Status);
            if (m.Error != null && !string.IsNullOrEmpty(m.Error.Message))
            {
                buildHadError = true;
                buildLogs.AppendLine(m.Error.Message);
            }
        });

        var buildParams = new ImageBuildParameters
        {
            Tags = new[] { imageTag },
            PullParent = false,
            Dockerfile = "Dockerfile"
        };

        await docker.Images.BuildImageFromDockerfileAsync(
            contents: buildContext,
            parameters: buildParams,
            authConfigs: null,
            headers: null,
            progress: progress,
            cancellationToken: CancellationToken.None
        );

        if (buildHadError)
        {
            return Results.Ok(new { buildError = true, output = buildLogs.ToString() });
        }

        // Run container (no network)
        var create = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = imageTag,
            HostConfig = new HostConfig
            {
                NetworkMode = "none",
                Memory = 256 * 1024 * 1024,
                PidsLimit = 128,
                ReadonlyRootfs = false,
                CapDrop = new[] { "ALL" },
                Ulimits = new[] { new Ulimit { Name = "nofile", Soft = 256, Hard = 256 } }
            },
            AttachStdout = true,
            AttachStderr = true,
            Tty = false
        });

        var id = create.ID;
        var output = new StringBuilder();

        try
        {
            await docker.Containers.StartContainerAsync(id, new ContainerStartParameters());
            var stream = await docker.Containers.AttachContainerAsync(id, false, new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true });
            var buffer = new byte[8192];
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!cts.IsCancellationRequested)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                if (read.EOF) break;
                output.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            }
            var wait = await docker.Containers.WaitContainerAsync(id);
            var exit = wait.StatusCode;
            return Results.Ok(new { exitCode = exit, output = output.ToString() });
        }
        finally
        {
            try { await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }); } catch {}
            try { await docker.Images.DeleteImageAsync(imageTag, new ImageDeleteParameters { Force = true }); } catch {}
        }
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, buildError = false });
    }
});

app.Run();

// Minimal TAR writer for gzipped tar streams
public sealed class TarWriter : IDisposable
{
    private readonly Stream _out;
    private bool _disposed;

    public TarWriter(Stream destination, bool leaveOpen = false)
    {
        _out = destination;
    }

    public void AddFile(string name, byte[] content)
    {
        var header = new byte[512];
        var nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
        var mode = Encoding.ASCII.GetBytes(Convert.ToString(420, 8).PadLeft(7, '0') + "\0"); // 0644
        Array.Copy(mode, 0, header, 100, mode.Length);
        var sizeOct = Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0");
        Array.Copy(sizeOct, 0, header, 124, sizeOct.Length);
        var timeOct = Encoding.ASCII.GetBytes(Convert.ToString((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8).PadLeft(11, '0') + "\0");
        Array.Copy(timeOct, 0, header, 136, timeOct.Length);
        header[156] = (byte)'0'; // regular file
        for (int i = 148; i < 156; i++) header[i] = 0x20;
        int sum = 0; foreach (var b in header) sum += b;
        var chk = Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
        Array.Copy(chk, 0, header, 148, chk.Length);

        _out.Write(header, 0, 512);
        _out.Write(content, 0, content.Length);
        var pad = (512 - (content.Length % 512)) % 512;
        if (pad > 0) _out.Write(new byte[pad], 0, pad);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _out.Write(new byte[1024], 0, 1024);
    }
}
