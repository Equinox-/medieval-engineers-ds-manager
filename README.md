## Medieval Engineers Dedicated Server Wrapper

### Features
- Automatic restart on crash
- Automatic restart on schedule
- Structured log collection
- Prometheus compatible metrics endpoint
- Discord integration for...
  - ... managing and restoring saves, including partial recovery
  - ... starting and stopping the server
  - ... player join/leave notifications
  - ... capturing profiling and debugging data
- Audit logs for determining what users did
- Partially reset world voxels via [a mod](https://steamcommunity.com/sharedfiles/filedetails/?id=2975680569)
- Various optimizations and bug fixes that have been backported from upcoming community edition versions

### Usage
- Download the [bootstrap](http://storage.googleapis.com/meds-dist/refs/heads/main/bootstrap.zip)
- Extract it into the folder you want to run the server in
- Run `Meds.Bootstrap.exe`
- Wait for the server to get installed
- Place your save file in `runtime/world`
