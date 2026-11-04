namespace PaxCommander.Platform.Win32;

/// <summary>Top-down 32-bit BGRA pixels, stride = Width * 4.</summary>
public sealed record RawImage(int Width, int Height, byte[] Pixels, bool Premultiplied);

/// <summary>
/// System image list sizes: Small 16 px, Large 32 px, ExtraLarge 48 px, Jumbo 256 px (Small/Large/ExtraLarge grow with
/// the display scaling the process starts under). The numbers are only identifiers; they are not the SHIL_* constants
/// (SHIL_LARGE is 0 and SHIL_SMALL is 1) — <see cref="ShellIcons"/> maps them.
/// </summary>
public enum ShellIconSize { Small = 0, Large = 1, ExtraLarge = 2, Jumbo = 4 }

/// <summary>What happened after a native context menu closed.</summary>
/// <param name="Verb">Canonical verb of the chosen command ("open", "rename", "delete", "properties"…), null if none or unknown.</param>
/// <param name="Invoked">True when the shell already ran the command; false when the app must perform <paramref name="Verb"/> itself.</param>
public sealed record ContextMenuResult(string? Verb, bool Invoked);

/// <summary>One item sitting in the Recycle Bin.</summary>
public sealed record RecycledEntry(string OriginalPath, DateTime DeletedUtc, long Size, string InfoFile, string DataFile);
