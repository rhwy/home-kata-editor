
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseSetting(WebHostDefaults.DetailedErrorsKey, "true");
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", async () =>
{
    try
    {
        var docker = CreateDocker();
        await docker.System.PingAsync();
        var runner = (await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
            .FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/').Equals("dotnet-runner", StringComparison.Ordinal)));
        return Results.Ok(new { docker = "ok", runner = runner?.State ?? "missing" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { docker = "error", error = ex.Message });
    }
});

app.MapGet("/work", async () =>
{
    var docker = CreateDocker();
    try
    {
        await WaitForDockerAsync(docker, TimeSpan.FromSeconds(10));
        var runnerId = await EnsureRunnerAsync(docker);
        var (exit, output) = await ExecAsync(docker, runnerId,
            new[] { "sh", "-lc", "pwd; echo; ls -la /work; echo; ls -la /out || true" },
            TimeSpan.FromSeconds(10));
        return Results.Text(output, "text/plain");
    }
    catch (Exception ex)
    {
        return Results.Text("[/work error] " + ex.Message, "text/plain");
    }
});

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

        var docker = CreateDocker();
        await WaitForDockerAsync(docker, TimeSpan.FromSeconds(20));

        var runnerId = await EnsureRunnerAsync(docker);

        var env = new[] {
            "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "DOTNET_NOLOGO=1",
            "NUGET_HTTP_CACHE_PATH=/root/.nuget/http",
            "NUGET_PACKAGES=/root/.nuget/packages"
        };
        var envString = string.Join(";", env);

        var csproj = GetRunnerCsproj();
        await PutFilesAsync(docker, runnerId, new Dictionary<string,string> {
            ["Runner.csproj"] = csproj,
            ["Program.cs"]    = code
        });

        var projectPath = "/work/Runner.csproj";

        // Initial OFFLINE restore to create project.assets.json
        var (r0Exit, r0Out) = await ExecAsync(docker, runnerId,
            new[] { "sh", "-lc", $"export {envString}; dotnet restore {projectPath} --disable-parallel --ignore-failed-sources" },
            TimeSpan.FromSeconds(30));
        if (r0Exit != 0)
        {
            var allowRestore = Environment.GetEnvironmentVariable("RUNNER_ALLOW_RESTORE") == "1";
            if (allowRestore)
            {
                var (r1Exit, r1Out) = await ExecAsync(docker, runnerId,
                    new[] { "sh", "-lc", $"export {envString}; dotnet restore {projectPath} --source https://api.nuget.org/v3/index.json --disable-parallel" },
                    TimeSpan.FromSeconds(45));
                if (r1Exit != 0)
                    return Results.Ok(new { buildError = true, output = r1Out });
            }
            else
            {
                return Results.Ok(new { buildError = true, output = r0Out });
            }
        }

        var (bExit, bOut) = await ExecAsync(docker, runnerId,
            new[] { "sh", "-lc", $"export {envString}; dotnet build {projectPath} -c Release -o /out --no-restore" },
            TimeSpan.FromSeconds(40));
        if (bExit != 0)
            return Results.Ok(new { buildError = true, output = bOut });

        var (xExit, xOut) = await ExecAsync(docker, runnerId,
            new[] { "dotnet", "/out/Runner.dll" },
            TimeSpan.FromSeconds(10));
        return Results.Ok(new { exitCode = xExit, output = xOut });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, buildError = false });
    }
});

app.Run();

// ---- Helpers ----

static DockerClient CreateDocker() =>
    new DockerClientConfiguration(new Uri(Environment.GetEnvironmentVariable("DOCKER_HOST") ?? "unix:///var/run/docker.sock")).CreateClient();

static async Task WaitForDockerAsync(DockerClient docker, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try { await docker.System.PingAsync(); return; }
        catch { if (DateTime.UtcNow > deadline) throw; await Task.Delay(500); }
    }
}

static string GetRunnerCsproj() => string.Join('\n', new[] {
    "<Project Sdk=\"Microsoft.NET.Sdk\">",
    "  <PropertyGroup>",
    "    <OutputType>Exe</OutputType>",
    "    <TargetFramework>net10.0</TargetFramework>",
    "    <ImplicitUsings>enable</ImplicitUsings>",
    "    <Nullable>enable</Nullable>",
    "  </PropertyGroup>",
    "</Project>",
    ""
});

static async Task<string> EnsureRunnerAsync(DockerClient docker)
{
    const string image = "mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview";
    try { await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = image }, null, new Progress<JSONMessage>()); } catch {}

    var list = await docker.Containers.ListContainersAsync(new ContainersListParameters { All = true });
    var existing = list.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/').Equals("dotnet-runner", StringComparison.Ordinal)));

    if (existing is null)
    {
        var created = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = image,
            Name = "dotnet-runner",
            WorkingDir = "/work",
            Cmd = new[] { "sh", "-lc", "mkdir -p /work && tail -f /dev/null" },
            HostConfig = new HostConfig
            {
                NetworkMode = "none",
                Binds = new List<string> { "runner-nuget:/root/.nuget/packages" },
                CapDrop = new[] { "ALL" },
                Memory = 512 * 1024 * 1024,
                PidsLimit = 256
            }
        });
        await docker.Containers.StartContainerAsync(created.ID, new ContainerStartParameters());
        return created.ID;
    }

    if (!string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
        await docker.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters());

    return existing.ID;
}

static async Task PutFilesAsync(DockerClient docker, string containerId, Dictionary<string,string> files)
{
    using var ms = new MemoryStream();
    using (var tw = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
    {
        foreach (var kv in files)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, kv.Key.Replace('\\','/'));
            entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            entry.ModificationTime = DateTimeOffset.UtcNow;
            var data = Encoding.UTF8.GetBytes(kv.Value);
            entry.DataStream = new MemoryStream(data);
            //entry.Size = data.Length;
            tw.WriteEntry(entry);
        }
    }
    ms.Position = 0;
    await docker.Containers.ExtractArchiveToContainerAsync(containerId, new ContainerPathStatParameters { Path = "/work" }, ms);
}

static async Task<(int exit, string output)> ExecAsync(DockerClient docker, string containerId, string[] cmd, TimeSpan? timeout = null)
{
    var exec = await docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
    {
        AttachStdout = true,
        AttachStderr = true,
        Tty = false,
        Cmd = cmd,
        WorkingDir = "/work"
    });

    using var stream = await docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false);
    var sb = new StringBuilder();
    var buf = new byte[8192];
    var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));

    try
    {
        while (!cts.IsCancellationRequested)
        {
            var read = await stream.ReadOutputAsync(buf, 0, buf.Length, cts.Token);
            if (read.EOF) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, read.Count));
        }
    }
    catch { }

    var inspect = await docker.Exec.InspectContainerExecAsync(exec.ID);
    var code = (int)inspect.ExitCode;
    return (code, sb.ToString());
}
