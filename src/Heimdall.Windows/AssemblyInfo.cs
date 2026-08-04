using System.Runtime.Versioning;

// The mirror image of Heimdall.Linux/AssemblyInfo.cs. Unlike Linux, Windows does
// have a real TFM (net10.0-windows) that would imply this — but the project
// stays on plain net10.0 so it still compiles on the Linux CI runner, and this
// attribute is what tells the platform analyser the same thing.
//
// The effect: every Windows-only API inside this project (the registry, the
// shell, SHGetKnownFolderPath) needs no per-call guard, and instead the *caller*
// guards once, at the single point where the platform is chosen.
[assembly: SupportedOSPlatform("windows")]
