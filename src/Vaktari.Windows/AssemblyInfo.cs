using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// The first InternalsVisibleTo in this repository, so it is worth saying why
// rather than letting it set a precedent by accident.
//
// Everything else in Vaktari.Windows.Tests goes through the public surface,
// which is the right default: a test that reaches inside pins the
// implementation rather than the behaviour. The network providers have a few
// pure functions that cannot be reached that way at all — turning a typed
// address into a UNC path only happens on the way into WNetAddConnection2, and
// choosing a mount URI for a service type only on the way out of a live mDNS
// sweep. Testing those through the public API would mean touching the network
// in a unit test; the alternative is making them public, which puts a helper on
// the platform's API surface to satisfy a test. This is the smaller cost.
[assembly: InternalsVisibleTo("Vaktari.Windows.Tests")]

// The mirror image of Vaktari.Linux/AssemblyInfo.cs. Unlike Linux, Windows does
// have a real TFM (net10.0-windows) that would imply this — but the project
// stays on plain net10.0 so it still compiles on the Linux CI runner, and this
// attribute is what tells the platform analyser the same thing.
//
// The effect: every Windows-only API inside this project (the registry, the
// shell, SHGetKnownFolderPath) needs no per-call guard, and instead the *caller*
// guards once, at the single point where the platform is chosen.
[assembly: SupportedOSPlatform("windows")]
