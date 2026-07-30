using System.CommandLine;
using Launcher.Cli;

// `vortex`: the CLI and the runner daemon in one binary. Typing a verb and running
// `vortex runner run` under a systemd unit are the same executable, which means one thing to install,
// version and package on a host box.

var json = new Option<bool>("--json")
{
    Description = "emit one JSON document on stdout instead of human text",
    Recursive = true,
};

// Every verb takes this so tests and CI can point the whole launcher at a scratch directory instead
// of the real per-user data root.
var dataRoot = new Option<string?>("--data-root")
{
    Description = "override the launcher data directory",
    Recursive = true,
};

var root = new RootCommand("vortex - Vortex Arena launcher, server runner and CLI");
root.Options.Add(json);
root.Options.Add(dataRoot);

PlayerCommands.Register(root, json, dataRoot);
BuildCommands.Register(root, json, dataRoot);
ServerCommands.Register(root, json, dataRoot);
RunnerCommands.Register(root, json, dataRoot);

return await root.Parse(args).InvokeAsync();
