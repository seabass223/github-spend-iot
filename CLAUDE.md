## Build
This project follows the Local Build Protocol (~/.claude/build-protocol/PROTOCOL.md).
Build only through `./build.sh` from Git Bash (`--target all`, `--run`, `--launch [--target <t>]`, `--pin`, `--stop`, `--info`);
output goes to `dist/`. Settings live in `build.config.json`. `build.sh` is a managed copy: don't edit it; change build
behaviour through the build-protocol agent.

Project specifics: type `dotnet`. A build runs the xunit tests, publishes `API/github-spend.csproj` to `dist/app`
(with the gitignored `API/local.settings.json` beside the exe; never print or commit it), then cloud-compiles
`IOT/photon_lcd_hello_world.ino` to `dist/firmware/photon_lcd_hello_world.bin` (needs `particle login`). Flashing is
never part of a build: `particle flash 3c003d001047353136383631 dist/firmware/photon_lcd_hello_world.bin`.
`./build.sh --launch` opens the server in a visible console window as the logged-in user (closing the window stops it; output is also appended to `dist/logs/app.log`; `dotnet.console` in `build.config.json`); `./build.sh --stop`
stops it and closes that window (also one left open after a crash), and a build fails while it runs unless `--force`
is passed (which stops it the same way). If something else holds `dist/app`, the build names it and keeps the previous build. A copy started with `dotnet run` is not managed
and locks `API/bin/Debug`. `API/tools/claude_usage_statusline.py` is referenced by absolute path from
`~/.claude/settings.json`: leave it in place.
