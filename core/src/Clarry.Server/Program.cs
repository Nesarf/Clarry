using Clarry.Server;

// The console host. Clarry is a standalone program: this exe serves the theatre, the stage page
// reads it over HTTP, and anything else that can speak HTTP -- a Unity client, a shell with curl,
// the optional DSH button -- is a client rather than a host.
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

ClarryHost.Build(args).Run();
