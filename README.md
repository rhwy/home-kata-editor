# Home code runner

The target of this project is to have a simple, free, hostable and deployable solution to run code katas collaboratively in teams.
It uses under the hood DinD (docker in docker) to make edited code running securely in a docker sandbox.
All is simply deployable through docker compose to hosted servers like Coolify.


## Run

Clone the repo, then compose:

```bash
docker compose up --build
# Open http://localhost:8080
```

First spin up could take some time (create the image and build), then next runs will be pretty fast (avg +1s over head on top of the normal .net build/run)
