using System.Collections.Frozen;

namespace PaxCommander.Search;

/// <summary>Extension groups behind <c>type:</c>, and the text/binary hints that <c>content:</c> uses.</summary>
internal static class QueryFileTypes
{
    [Flags]
    internal enum Group
    {
        None = 0,
        Image = 1 << 0,
        Video = 1 << 1,
        Audio = 1 << 2,
        Doc = 1 << 3,
        Archive = 1 << 4,
        Code = 1 << 5,
        Folder = 1 << 6,
        File = 1 << 7,
    }

    // Each extension belongs to at most one group so type: results never overlap. Where formats collide the developer
    // reading wins: .ts is TypeScript (not MPEG-TS video), .mts stays camcorder video, .vhd stays a disk image.
    private const string ImageExts =
        "jpg jpeg jpe jfif pjpeg pjp png apng gif bmp dib tif tiff webp heic heif avif jxl jp2 j2k jpf jpx ico cur svg svgz " +
        "psd psb raw cr2 cr3 crw nef nrw arw srf sr2 dng orf rw2 raf srw pef x3f erf kdc mrw 3fr tga exr hdr xcf kra " +
        "eps ai emf wmf pcx ppm pgm pbm pnm qoi dds wdp jxr";
    private const string VideoExts =
        "mp4 m4v mkv webm avi mov qt wmv flv f4v mpg mpeg mpe mpv m2v mts m2ts vob 3gp 3g2 ogv rm rmvb asf divx xvid " +
        "mxf y4m dv h264 h265 hevc vp9 av1 ivf";
    private const string AudioExts =
        "mp3 wav wave flac aac m4a m4b m4p ogg oga opus wma aiff aif aifc alac ape mid midi kar amr au snd mka wv dsf dff " +
        "ac3 eac3 dts caf ra spx tta mpc gsm voc w64 xm mod s3m it";
    private const string DocExts =
        "pdf doc docx docm dot dotx dotm odt ott rtf txt text md markdown rst adoc tex xls xlsx xlsm xlsb xlt xltx ods ots " +
        "csv tsv ppt pptx pptm pps ppsx pot potx odp otp epub mobi azw azw3 djvu xps oxps pages numbers key one vsd vsdx " +
        "wpd wps msg eml chm";
    private const string ArchiveExts =
        "zip zipx 7z rar tar gz tgz bz2 tbz tbz2 xz txz zst tzst lz lzma lz4 z cab iso img vhd vhdx wim esd dmg jar war ear " +
        "apk aab xapk ipa deb rpm arj lzh lha cpio nupkg snupkg vsix appx appxbundle msix msixbundle crx xpi whl";
    private const string CodeExts =
        "cs csx vb fs fsx fsi xaml axaml csproj vbproj fsproj vcxproj sln slnx props targets nuspec resx razor cshtml " +
        "c h cpp cc cxx hpp hh hxx inl ipp m mm java kt kts scala groovy gradle go rs swift dart py pyw pyi pyx ipynb rb " +
        "php pl pm lua r jl js mjs cjs jsx ts tsx cts vue svelte astro html htm xhtml css scss sass less json jsonc json5 " +
        "xml xsd xsl xslt yaml yml toml ini cfg conf config sql sh bash zsh fish ps1 psm1 psd1 bat cmd vbs asm s cmake " +
        "mk proto graphql gql hlsl glsl shader wgsl cu zig nim ex exs erl hrl hs elm clj cljs ml mli pas d v sv vhdl tf " +
        "hcl editorconfig gitignore gitattributes dockerfile makefile";

    // content: reads these without sniffing (a stray NUL byte will not disqualify them).
    private const string TextExts =
        "txt text md markdown mdx rst adoc asciidoc org tex latex bib log csv tsv psv json jsonc json5 jsonl ndjson geojson " +
        "xml xsd xsl xslt xaml axaml svg html htm xhtml mht mhtml css scss sass less styl js mjs cjs jsx ts tsx cts vue svelte " +
        "astro yaml yml toml ini cfg conf config properties prop env editorconfig gitignore gitattributes gitmodules " +
        "dockerignore npmrc nvmrc prettierrc eslintrc babelrc cs csx vb fs fsx fsi csproj vbproj fsproj vcxproj filters " +
        "sln slnx props targets nuspec resx razor cshtml vbhtml aspx ascx asax ashx asmx master c h cpp cc cxx hpp hh hxx " +
        "inl ipp tpp m mm java kt kts scala sc groovy gradle go rs swift dart py pyw pyi pyx ipynb rb rake gemspec erb php " +
        "phtml pl pm pod lua r rmd jl sh bash zsh fish ksh csh ps1 psm1 psd1 ps1xml bat cmd vbs vba bas frm asm s nasm inc " +
        "cmake mk mak make gn gni bzl bazel ninja proto thrift graphql gql hlsl glsl vert frag comp geom shader cginc " +
        "wgsl metal cu cuh cl zig nim ex exs eex heex erl hrl hs lhs elm clj cljs cljc edn ml mli pas pp dpr lpr d v sv svh " +
        "vhdl tf tfvars hcl sql psql prisma http rest srt vtt ass ssa lrc cue m3u m3u8 pls nfo diz rtf reg inf url desktop " +
        "service manifest appxmanifest lock sum patch diff po pot strings ftl mustache hbs handlebars ejs pug jade haml " +
        "slim liquid njk twig j2 jinja tmpl tpl eml dockerfile makefile";

    // content: never opens these: they are compressed or binary containers, so scanning them only costs IO.
    private const string ExtraBinaryExts =
        "exe dll sys drv ocx cpl scr efi mui msi msp msu pdb o a lib so dylib ko bin class dex pyc pyo pyd egg wasm node " +
        "ttf otf ttc woff woff2 eot fon swf fla blend fbx max 3ds dwg psd psb indd sketch fig xcf kra lnk icns ani dmp " +
        "mdmp hdmp etl evtx evt pf cat winmd tlb olb res db sqlite sqlite3 db3 mdb accdb ldf mdf ndf pst ost vmdk vdi " +
        "qcow2 pak unitypackage";

    private static readonly FrozenDictionary<string, Group> s_groups = BuildGroups();
    private static readonly FrozenDictionary<string, Group>.AlternateLookup<ReadOnlySpan<char>> s_groupLookup =
        s_groups.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string> s_text = Words(TextExts).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> s_textLookup =
        s_text.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenSet<string> s_binary = Words(ExtraBinaryExts)
        .Concat(Words(ImageExts)).Concat(Words(VideoExts)).Concat(Words(AudioExts)).Concat(Words(ArchiveExts))
        .Concat(["pdf", "doc", "docx", "docm", "dot", "dotx", "dotm", "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "ppt",
            "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odt", "ott", "ods", "ots", "odp", "otp", "epub", "mobi", "azw",
            "azw3", "djvu", "xps", "oxps", "one", "vsd", "vsdx", "chm", "msg", "pages", "numbers", "key"])
        .Where(e => !s_text.Contains(e)) // svg, eps-like text formats stay readable
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> s_binaryLookup =
        s_binary.GetAlternateLookup<ReadOnlySpan<char>>();

    private static IEnumerable<string> Words(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static FrozenDictionary<string, Group> BuildGroups()
    {
        var map = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        void Add(string list, Group g)
        {
            foreach (var e in Words(list)) map.TryAdd(e, g); // first group listed wins on a collision
        }
        Add(ImageExts, Group.Image);
        Add(VideoExts, Group.Video);
        Add(AudioExts, Group.Audio);
        Add(ArchiveExts, Group.Archive);
        Add(DocExts, Group.Doc);
        Add(CodeExts, Group.Code);
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The extension of a file name without the dot ("" when there is none). ".gitignore" → "gitignore".</summary>
    internal static ReadOnlySpan<char> ExtensionOf(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? default : name[(dot + 1)..];
    }

    internal static Group GroupOf(ReadOnlySpan<char> ext) =>
        !ext.IsEmpty && s_groupLookup.TryGetValue(ext, out var g) ? g : Group.None;

    internal static bool IsKnownText(ReadOnlySpan<char> ext) => !ext.IsEmpty && s_textLookup.Contains(ext);

    internal static bool IsKnownBinary(ReadOnlySpan<char> ext) => !ext.IsEmpty && s_binaryLookup.Contains(ext);

    /// <summary>Parses one type: word (singular, plural and a few synonyms are accepted).</summary>
    internal static bool TryParseGroup(ReadOnlySpan<char> word, out Group group)
    {
        group = word.ToString().ToLowerInvariant() switch
        {
            "image" or "images" or "img" or "picture" or "pictures" or "pic" or "pics" or "photo" or "photos" => Group.Image,
            "video" or "videos" or "movie" or "movies" or "vid" => Group.Video,
            "audio" or "music" or "sound" or "sounds" or "song" or "songs" => Group.Audio,
            "doc" or "docs" or "document" or "documents" => Group.Doc,
            "archive" or "archives" or "compressed" or "zip" => Group.Archive,
            "code" or "source" or "src" or "script" or "scripts" => Group.Code,
            "folder" or "folders" or "dir" or "dirs" or "directory" or "directories" => Group.Folder,
            "file" or "files" => Group.File,
            _ => Group.None,
        };
        return group != Group.None;
    }

    internal static string Describe(Group g, bool negated = false)
    {
        var parts = new List<string>(4);
        if (g.HasFlag(Group.Image)) parts.Add("images");
        if (g.HasFlag(Group.Video)) parts.Add("videos");
        if (g.HasFlag(Group.Audio)) parts.Add("audio");
        if (g.HasFlag(Group.Doc)) parts.Add("documents");
        if (g.HasFlag(Group.Archive)) parts.Add("archives");
        if (g.HasFlag(Group.Code)) parts.Add("code");
        if (g.HasFlag(Group.File)) parts.Add(parts.Count == 0 && !g.HasFlag(Group.Folder) && !negated ? "files only" : "files");
        if (g.HasFlag(Group.Folder)) parts.Add(parts.Count == 0 && !negated ? "folders only" : "folders");
        return string.Join(" or ", parts);
    }
}
