using System.Runtime.CompilerServices;
using IpScanner.Core.Localization;

namespace IpScanner.Tests;

/// <summary>Pins the UI language to English before any test runs. Many tests switch the static
/// language and restore "en"; without a fixed start, the first such restore flips the language under a
/// parallel test on a machine whose system language differs.</summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void Init() => Loc.SetLanguage("en");
}
