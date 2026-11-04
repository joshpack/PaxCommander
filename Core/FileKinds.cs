namespace PaxCommander.Core;

public enum FileKind { Folder, Drive, Image, Video, Audio, Document, Text, Code, Archive, Executable, Other }

/// <summary>Coarse file categories for fallback icons, previews and type colours.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExt = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Add(FileKind.Image, ".jpg .jpeg .jfif .png .gif .bmp .webp .tif .tiff .heic .heif .avif .ico .svg .raw .cr2 .cr3 .nef .arw .dng .psd .jxl");
        Add(FileKind.Video, ".mp4 .mkv .avi .mov .wmv .webm .m4v .mpg .mpeg .ts .m2ts .flv .3gp .vob .ogv");
        Add(FileKind.Audio, ".mp3 .flac .wav .m4a .aac .ogg .opus .wma .aiff .aif .alac .mka .m4b");
        Add(FileKind.Document, ".pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .rtf .epub .mobi .pages .numbers .key");
        Add(FileKind.Text, ".txt .md .markdown .log .csv .tsv .ini .cfg .conf .nfo .srt .vtt .ass .env .gitignore .gitattributes .editorconfig .rst .tex .dockerignore .npmrc .gitmodules .gitconfig .prettierrc .eslintrc .babelrc .properties .reg .inf .out .err .lst .diff .patch");
        Add(FileKind.Code, ".cs .csproj .sln .slnx .axaml .xaml .js .mjs .cjs .ts .tsx .jsx .py .java .kt .kts .c .cc .cpp .cxx .h .hpp .go .rs .rb .php .html .htm .css .scss .sass .less .json .jsonc .xml .yaml .yml .toml .sh .bash .zsh .ps1 .psm1 .psd1 .bat .cmd .sql .swift .dart .lua .r .pl .vue .svelte .gradle .props .targets .dockerfile .make .cmake .fs .vb .ex .exs .zig .nim .proto .graphql");
        Add(FileKind.Archive, ".zip .7z .rar .tar .gz .tgz .bz2 .xz .zst .iso .cab .lz .lzma .jar .apk .msix .appx .nupkg .whl");
        Add(FileKind.Executable, ".exe .msi .com .scr .appref-ms .lnk .url .dll .sys .cpl .msc");
    }

    private static void Add(FileKind kind, string exts)
    {
        foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            ByExt[e] = kind;
    }

    public static FileKind Of(FileItem item) => item.Kind switch
    {
        ItemKind.Folder => FileKind.Folder,
        ItemKind.Drive => FileKind.Drive,
        _ => OfExtension(item.Extension),
    };

    public static FileKind OfExtension(string ext) => ByExt.TryGetValue(ext, out var k) ? k : FileKind.Other;

    /// <summary>Text-like files the preview pane shows as text.</summary>
    public static bool IsTextual(string ext) =>
        OfExtension(ext) is FileKind.Text or FileKind.Code || ext is "" or ".svg";

    /// <summary>Images Avalonia (Skia) can decode itself, so previews work without the shell.</summary>
    public static bool IsDecodableImage(string ext) =>
        ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico";

    /// <summary>Generic type name used when the shell can't supply one: "PNG File", "File folder".</summary>
    public static string GenericTypeName(FileItem item) => item.Kind switch
    {
        ItemKind.Folder => "File folder",
        ItemKind.Drive => item.DriveDescription ?? "Drive",
        _ => item.Extension.Length > 1 ? item.Extension[1..].ToUpperInvariant() + " File" : "File",
    };
}
