using Clarry.Server;

// The console host. Clarry is a standalone program: this exe serves the theatre, the stage page
// reads it over HTTP, and anything else that can speak HTTP -- a Unity client, a shell with curl,
// the desktop window -- is a client rather than a host.
//
// The assembly lives in ClarryHost.Build so the desktop window (Clarry.Desktop) can start the
// very same world inside its own process. Adding a second copy of this file would be the one
// change that makes the two disagree about who is on stage.
//
// Two ways to advance the story:
//   manual  -- /api/round/begin, /api/round/order, /api/round/resolve, one call at a time
//   driven  -- /api/driver/attach then /api/loop/start, and the theatre runs itself
// Both take the same gate, so they never interleave.

// The story is UTF-8 from end to end. A Windows console defaults to the system code page,
// which turns every character name in the log into question marks -- so pin the output here.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// The log is a file this process opens itself, not the stdout of whoever happened to start it.
//
// Start-Process (or a terminal, or a CI step) hands over a handle owned by the parent. When that
// parent goes away -- the shell that launched us closes, a tool call returns, the step ends -- the
// next Console.WriteLine has nowhere to go, and the process disappears with an empty error log.
// It looks exactly like "the server died on its own", and it cost more than one afternoon here.
// Owning the file makes the server independent of how it was launched.
var root = Environment.GetEnvironmentVariable("CLARRY_ROOT") ?? @"D:\Clarry";
var dataDir = Environment.GetEnvironmentVariable("CLARRY_DATA") ?? Path.Combine(root, "data");
try
{
    Directory.CreateDirectory(dataDir);
    var log = new StreamWriter(
        new FileStream(Path.Combine(dataDir, "server.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        System.Text.Encoding.UTF8)
    { AutoFlush = true };
    Console.SetOut(log);
    Console.WriteLine($"--- Clarry.Server {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} root={root} ---");
}
catch
{
    // A theatre that cannot open its log still runs; losing the log is better than losing the show.
}

ClarryHost.Build(args).Run();
