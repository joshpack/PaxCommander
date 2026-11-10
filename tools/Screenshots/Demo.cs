using System.Diagnostics;
using System.Text;
using SkiaSharp;

namespace pxdCommander.Screenshots;

/// <summary>
/// The sample folders the screenshots show: a few documents, downloads, a photo album, wallpapers and a small web
/// project with git history. Everything is made up; nothing comes from this PC.
/// </summary>
internal static class Demo
{
    public static void Build(string root)
    {
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        var now = DateTime.Now;

        foreach (var d in new[] { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Projects", "Videos" })
            Directory.CreateDirectory(Path.Combine(root, d));

        // Documents
        var docs = Path.Combine(root, "Documents");
        foreach (var d in new[] { "Recipes", "Scans", "Taxes 2026" })
        {
            Directory.CreateDirectory(Path.Combine(docs, d));
            File.WriteAllText(Path.Combine(docs, d, "notes.txt"), "Sample file.");
        }
        Blob(Path.Combine(docs, "Household budget.xlsx"), 18_400, now.AddDays(-12));
        Blob(Path.Combine(docs, "Invoice-1042.pdf"), 96_200, now.AddDays(-9));
        Blob(Path.Combine(docs, "Lease renewal.docx"), 41_700, now.AddDays(-20));
        Text(Path.Combine(docs, "Meeting notes.md"), "# Planning\n\n- Ship the forecast page\n- Unit tests for conversions\n", now.AddDays(-2));
        Text(Path.Combine(docs, "Packing list.txt"), "Rain jacket\nHiking boots\nCamera\nCharger\n", now.AddDays(-30));
        Blob(Path.Combine(docs, "Quarterly report.pptx"), 1_240_000, now.AddDays(-6));

        // Downloads
        var dl = Path.Combine(root, "Downloads");
        Blob(Path.Combine(dl, "Camera manual.pdf"), 145_400, now.AddDays(-18));
        Text(Path.Combine(dl, "settings-export.json"), "{\n  \"theme\": \"dark\",\n  \"units\": \"metric\"\n}\n", now.AddDays(-2).Date.AddHours(18).AddMinutes(57));
        Blob(Path.Combine(dl, "Fonts.zip"), 8_388_608, now.AddDays(-12));
        Blob(Path.Combine(dl, "Tool setup.msi"), 31_560_000, now.AddDays(-3).Date.AddHours(19).AddMinutes(17));
        Blob(Path.Combine(dl, "Podcast episode 12.mp3"), 41_943_040, now.AddDays(-13));

        // Music and videos
        Blob(Path.Combine(root, "Music", "Morning walk.mp3"), 5_200_000, now.AddDays(-40));
        Blob(Path.Combine(root, "Music", "Night drive.mp3"), 6_100_000, now.AddDays(-41));
        Blob(Path.Combine(root, "Videos", "Glacier hike.mp4"), 12_400_000, now.AddDays(-25));

        // Pictures
        var album = Path.Combine(root, "Pictures", "Iceland 2026");
        var walls = Path.Combine(root, "Pictures", "Wallpapers");
        Directory.CreateDirectory(album);
        Directory.CreateDirectory(walls);
        var day = new DateTime(2026, 10, 6, 19, 0, 0);
        var shots = new (string Name, Scene Scene)[]
        {
            ("Aurora over the fjord", Scene.Aurora), ("Ring road", Scene.Day), ("Reykjavik harbour", Scene.Dusk),
            ("Midnight sun", Scene.Sunset), ("Skogafoss", Scene.Green), ("Sunset at Vik", Scene.Fire),
            ("Black sand beach", Scene.Grey), ("Northern lights", Scene.Aurora2), ("Glacier lagoon", Scene.Ice),
        };
        for (var i = 0; i < shots.Length; i++)
            Landscape(Path.Combine(album, shots[i].Name + ".png"), 1600, 1060, shots[i].Scene, i + 1, day.AddDays(i).AddMinutes(i * 7));
        Landscape(Path.Combine(walls, "Night city.png"), 1600, 1060, Scene.Night, 31, new DateTime(2026, 10, 10, 9, 12, 0));
        Landscape(Path.Combine(walls, "Dunes.png"), 1600, 1060, Scene.Sand, 32, new DateTime(2026, 10, 11, 9, 40, 0));
        Landscape(Path.Combine(root, "Desktop", "Wallpaper.png"), 1600, 1060, Scene.Dusk, 33, new DateTime(2026, 10, 9, 8, 30, 0));

        WeatherApp(Path.Combine(root, "Projects", "weather-app"), now);

        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(d => !d.Contains(".git")))
            Directory.SetLastWriteTime(d, now.AddMinutes(-1));
    }

    private static void WeatherApp(string app, DateTime now)
    {
        Directory.CreateDirectory(Path.Combine(app, "public"));
        Directory.CreateDirectory(Path.Combine(app, "src"));
        Directory.CreateDirectory(Path.Combine(app, "tests"));
        var t = now.Date.AddHours(18).AddMinutes(51);

        Text(Path.Combine(app, ".gitignore"), "node_modules/\ndist/\n", now.Date.AddHours(19).AddMinutes(17));
        Text(Path.Combine(app, "package.json"), """
            {
              "name": "weather-app",
              "private": true,
              "type": "module",
              "scripts": { "dev": "vite", "build": "vite build", "test": "vitest" },
              "dependencies": { "preact": "^10.24.0" },
              "devDependencies": { "vite": "^6.0.0", "vitest": "^2.1.0" }
            }

            """, t);
        Text(Path.Combine(app, "README.md"), "# weather-app\n\nA seven-day forecast in the browser, built with Vite and Preact.\n\n```\nnpm install\nnpm run dev\n```\n", t);
        Text(Path.Combine(app, "vite.config.js"), "import { defineConfig } from 'vite';\nimport preact from '@preact/preset-vite';\n\nexport default defineConfig({\n  plugins: [preact()],\n});\n", now.Date.AddHours(19).AddMinutes(15));
        Text(Path.Combine(app, "public", "index.html"), "<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><title>Weather</title></head>\n<body><div id=\"app\"></div><script type=\"module\" src=\"/src/main.jsx\"></script></body>\n</html>\n" + new string(' ', 9000), t);
        Text(Path.Combine(app, "src", "App.jsx"), "import { useEffect, useState } from 'preact/hooks';\nimport { getForecast } from './forecast.js';\nimport { toFahrenheit } from './units.js';\n\nexport function App() {\n  const [days, setDays] = useState([]);\n  useEffect(() => { getForecast(64.14, -21.94).then(setDays); }, []);\n  return <ul>{days.map(d => <li key={d.date}>{d.date}: {d.high}° / {d.low}°</li>)}</ul>;\n}\n", now.Date.AddHours(19).AddMinutes(4));
        Text(Path.Combine(app, "src", "main.jsx"), "import { render } from 'preact';\nimport { App } from './App.jsx';\n\nrender(<App />, document.getElementById('app'));\n", t);
        Text(Path.Combine(app, "src", "units.js"), "export const toFahrenheit = c => Math.round(c * 9 / 5 + 32);\nexport const toMph = kmh => Math.round(kmh / 1.609);\nexport const toInches = mm => +(mm / 25.4).toFixed(2);\n", t);
        Text(Path.Combine(app, "src", "styles.css"), "body {\n  font-family: system-ui, sans-serif;\n  background: #0b1020;\n  color: #e6e9f2;\n}\n\nli { padding: 6px 0; }\n", t);
        Text(Path.Combine(app, "src", "forecast.js"), Forecast, now.Date.AddHours(19).AddMinutes(2));
        Text(Path.Combine(app, "tests", "units.test.js"), "import { expect, test } from 'vitest';\nimport { toFahrenheit } from '../src/units.js';\n\ntest('freezing', () => expect(toFahrenheit(0)).toBe(32));\n", t);

        // A short, made-up history, then some work in progress (two edits and a new file).
        Git(app, "init", "-q", "-b", "main");
        var when = now.Date.AddDays(-3).AddHours(10);
        Commit(app, "Start weather-app with Vite and Preact", when, "package.json", "vite.config.js", "public", "src/main.jsx", ".gitignore");
        Commit(app, "Add the App shell", when.AddHours(2), "src/App.jsx");
        Commit(app, "Fetch the 7-day forecast from Open-Meteo", when.AddDays(1), "src/forecast.js");
        Commit(app, "Add unit conversions", when.AddDays(1).AddHours(3), "src/units.js", "src/styles.css");
        Commit(app, "Tests for units", when.AddDays(2), "tests");
        Commit(app, "Add a README", when.AddDays(2).AddHours(1), "README.md");

        File.AppendAllText(Path.Combine(app, "README.md"), "\nForecasts come from the free Open-Meteo API.\n");
        File.AppendAllText(Path.Combine(app, "src", "styles.css"), "\n.today { font-weight: 600; }\n");
        Text(Path.Combine(app, "src", "cache.js"), "export const cache = new Map();\n", t);
        File.SetLastWriteTime(Path.Combine(app, "src", "styles.css"), now);
        File.SetLastWriteTime(Path.Combine(app, "README.md"), now.AddSeconds(-50));
    }

    private const string Forecast = """
        // Seven-day forecast from the public Open-Meteo API, cached for ten minutes per location.
        const API = 'https://api.open-meteo.com/v1/forecast';
        const TTL = 10 * 60 * 1000;
        const cache = new Map();

        /**
         * @param {number} lat
         * @param {number} lon
         * @returns {Promise<{ date: string, high: number, low: number, rain: number }[]>}
         */
        export async function getForecast(lat, lon) {
          const key = `${lat.toFixed(2)},${lon.toFixed(2)}`;
          const hit = cache.get(key);
          if (hit && Date.now() - hit.at < TTL) return hit.days;

          const url = `${API}?latitude=${lat}&longitude=${lon}` +
            '&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto';
          const res = await fetch(url);
          if (!res.ok) throw new Error(`Forecast failed: ${res.status}`);
          const json = await res.json();

          const days = json.daily.time.map((date, i) => ({
            date,
            high: Math.round(json.daily.temperature_2m_max[i]),
            low: Math.round(json.daily.temperature_2m_min[i]),
            rain: json.daily.precipitation_sum[i],
          }));
          cache.set(key, { at: Date.now(), days });
          return days;
        }

        """;

    private static void Commit(string repo, string message, DateTime when, params string[] paths)
    {
        Git(repo, ["add", .. paths]);
        var stamp = when.ToString("yyyy-MM-ddTHH:mm:ss");
        Git(repo, new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = stamp, ["GIT_COMMITTER_DATE"] = stamp },
            "-c", "user.name=Demo", "-c", "user.email=demo@example.com", "commit", "-q", "-m", message);
    }

    private static void Git(string repo, params string[] args) => Git(repo, null, args);

    private static void Git(string repo, Dictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err}");
    }

    private static void Text(string path, string text, DateTime time)
    {
        File.WriteAllText(path, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);
    }

    private static void Blob(string path, long size, DateTime time)
    {
        using (var f = File.Create(path))
        {
            var rnd = new Random(path.Length);
            var buf = new byte[65536];
            for (long done = 0; done < size; done += buf.Length)
            {
                rnd.NextBytes(buf);
                f.Write(buf, 0, (int)Math.Min(buf.Length, size - done));
            }
        }
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);
    }

    public enum Scene { Day, Dusk, Sunset, Fire, Green, Grey, Ice, Aurora, Aurora2, Night, Sand }

    /// <summary>A simple painted landscape: sky, sun or aurora, two mountain ridges and water.</summary>
    public static void Landscape(string path, int w, int h, Scene scene, int seed, DateTime time)
    {
        var (top, bottom, far, near, water, sun) = scene switch
        {
            Scene.Day => ("#8FC7E8", "#DCEFF8", "#6F8A9A", "#3E5560", "#9CC9D9", "#FFF3C4"),
            Scene.Dusk => ("#5B6C8F", "#E8B98A", "#56607A", "#2E3548", "#7C8DA8", "#FFE2B0"),
            Scene.Sunset => ("#F2A65A", "#F7D9A0", "#6E6A78", "#3A3842", "#E9B97E", "#FFF4D6"),
            Scene.Fire => ("#E2563A", "#F6A54C", "#5A3A44", "#2B1E26", "#C9653F", "#FFE1A8"),
            Scene.Green => ("#9AC8D8", "#E2F0E8", "#5F7D66", "#34503D", "#8FB8B0", "#FFF6D0"),
            Scene.Grey => ("#8C939C", "#C9CDD2", "#5E6670", "#2F343A", "#A7AFB8", "#F4F4EE"),
            Scene.Ice => ("#7FB7DE", "#CFE7F5", "#5E7F96", "#3B5568", "#BFE3F2", "#FFF8DC"),
            Scene.Aurora or Scene.Aurora2 => ("#06121F", "#10283A", "#1E2A36", "#0E161E", "#123447", ""),
            Scene.Night => ("#0B1030", "#2A2858", "#1B1D3A", "#0E0F22", "#232A55", "#F6E7B0"),
            _ => ("#F0C27B", "#F8E3B5", "#C98D52", "#9A6235", "#E8B77D", "#FFF7DD"),
        };
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var c = new SKCanvas(bmp))
        {
            var rnd = new Random(seed);
            var horizon = h * 0.72f;
            using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, horizon), [SKColor.Parse(top), SKColor.Parse(bottom)], SKShaderTileMode.Clamp) })
                c.DrawRect(0, 0, w, horizon + 2, sky);

            if (scene is Scene.Aurora or Scene.Aurora2 or Scene.Night)
                using (var star = new SKPaint { Color = new SKColor(255, 255, 255, 150), IsAntialias = true })
                    for (var i = 0; i < 160; i++) c.DrawCircle(rnd.Next(w), rnd.Next((int)(horizon * 0.8)), (float)rnd.NextDouble() * 1.6f + 0.4f, star);

            if (scene is Scene.Aurora or Scene.Aurora2)
            {
                for (var band = 0; band < 4; band++)
                {
                    using var p = new SKPath();
                    var y0 = h * (0.18f + band * 0.07f);
                    p.MoveTo(0, y0);
                    for (var x = 0; x <= w; x += 40) p.LineTo(x, y0 + (float)Math.Sin(x / 180.0 + band + seed) * h * 0.05f);
                    using var glow = new SKPaint
                    {
                        Color = band % 2 == 0 ? new SKColor(60, 230, 150, 120) : new SKColor(40, 190, 170, 90),
                        Style = SKPaintStyle.Stroke, StrokeWidth = h * 0.05f, IsAntialias = true,
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, h * 0.02f),
                    };
                    c.DrawPath(p, glow);
                }
            }
            else if (sun.Length > 0)
            {
                using var s = new SKPaint { Color = SKColor.Parse(sun), IsAntialias = true };
                c.DrawCircle(w * (0.25f + (float)rnd.NextDouble() * 0.5f), h * (0.18f + (float)rnd.NextDouble() * 0.15f), h * 0.045f, s);
            }

            Ridge(c, rnd, w, horizon, h * 0.32f, SKColor.Parse(far), 9);
            Ridge(c, rnd, w, horizon, h * 0.2f, SKColor.Parse(near), 14);

            using (var wat = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, horizon), new SKPoint(0, h), [SKColor.Parse(water), SKColor.Parse(near)], SKShaderTileMode.Clamp) })
                c.DrawRect(0, horizon, w, h - horizon, wat);
            using (var shine = new SKPaint { Color = new SKColor(255, 255, 255, 60) })
                c.DrawRect(0, horizon, w, 3, shine);
        }

        // A little grain, like a real photo (and realistic file sizes).
        var px = bmp.GetPixelSpan();
        var noise = new Random(seed * 7919);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(px).ToArray();
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var n = noise.Next(-3, 4);
            bytes[i] = (byte)Math.Clamp(bytes[i] + n, 0, 255);
            bytes[i + 1] = (byte)Math.Clamp(bytes[i + 1] + n, 0, 255);
            bytes[i + 2] = (byte)Math.Clamp(bytes[i + 2] + n, 0, 255);
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        using (var f = File.Create(path)) data.SaveTo(f);
        File.SetCreationTime(path, time);
        File.SetLastWriteTime(path, time);
    }

    private static void Ridge(SKCanvas c, Random rnd, int w, float baseY, float height, SKColor color, int peaks)
    {
        using var p = new SKPath();
        p.MoveTo(0, baseY);
        var step = (float)w / peaks;
        for (var i = 0; i <= peaks; i++)
        {
            var x = i * step;
            p.LineTo(x + step * 0.5f * (float)rnd.NextDouble(), baseY - height * (0.45f + 0.55f * (float)rnd.NextDouble()));
            p.LineTo(x + step * (0.6f + 0.4f * (float)rnd.NextDouble()), baseY - height * 0.25f * (float)rnd.NextDouble());
        }
        p.LineTo(w, baseY);
        p.Close();
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawPath(p, paint);
    }
}
