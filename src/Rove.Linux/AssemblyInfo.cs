using System.Runtime.Versioning;

// The whole assembly is Linux-only. There is no net10.0-linux TFM — .NET only
// defines OS-specific frameworks for windows, android, ios, macos, maccatalyst,
// tvos and browser — so this attribute is how the platform analyser is told.
//
// The effect: every Linux-only API inside this project (GetUnixFileMode,
// SetUnixFileMode, /proc, /dev) needs no per-call guard, and instead the
// *callers* must guard once, at the single point where the platform is chosen.
[assembly: SupportedOSPlatform("linux")]
