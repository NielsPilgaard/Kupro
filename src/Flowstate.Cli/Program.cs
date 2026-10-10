using System.CommandLine;

var root = new RootCommand("Flowstate CLI — agent-first issue tracking from the terminal.");

return root.Parse(args).Invoke();
