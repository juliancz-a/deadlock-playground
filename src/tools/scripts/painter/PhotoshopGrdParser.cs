using Godot;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeadlockPlayground.Painter
{
    public struct GradientStop
    {
        public float Position; // 0.0f to 1.0f
        public Color Color;
        public float Midpoint; // 0.0f to 1.0f, default 0.5f

        public GradientStop(float position, Color color, float midpoint = 0.5f)
        {
            Position = Mathf.Clamp(position, 0f, 1f);
            Color = color;
            Midpoint = Mathf.Clamp(midpoint, 0.01f, 0.99f);
        }
    }

    public struct GradientTransparencyStop
    {
        public float Position; // 0.0f to 1.0f
        public float Opacity;  // 0.0f to 1.0f
        public float Midpoint; // default 0.5f

        public GradientTransparencyStop(float position, float opacity, float midpoint = 0.5f)
        {
            Position = Mathf.Clamp(position, 0f, 1f);
            Opacity = Mathf.Clamp(opacity, 0f, 1f);
            Midpoint = Mathf.Clamp(midpoint, 0.01f, 0.99f);
        }
    }

    public class GradientDefinition
    {
        public string Name { get; set; } = "Gradient";
        public List<GradientStop> Stops { get; set; } = new();
        public List<GradientTransparencyStop> TransparencyStops { get; set; } = new();

        public GradientDefinition() { }

        public GradientDefinition(string name, params (float pos, Color col)[] stops)
        {
            Name = name;
            foreach (var (pos, col) in stops)
            {
                Stops.Add(new GradientStop(pos, col));
            }
            Stops.Sort((a, b) => a.Position.CompareTo(b.Position));
        }

        public GradientDefinition Clone()
        {
            var copy = new GradientDefinition
            {
                Name = Name,
                Stops = new List<GradientStop>(Stops),
                TransparencyStops = new List<GradientTransparencyStop>(TransparencyStops)
            };
            return copy;
        }

        private static readonly float[] s_bayer4x4 = new float[16] {
             0f / 16f,  8f / 16f,  2f / 16f, 10f / 16f,
            12f / 16f,  4f / 16f, 14f / 16f,  6f / 16f,
             3f / 16f, 11f / 16f,  1f / 16f,  9f / 16f,
            15f / 16f,  7f / 16f, 13f / 16f,  5f / 16f
        };

        public Color Evaluate(float t, bool reverse = false, bool dither = false, int x = 0, int y = 0)
        {
            if (reverse) t = 1.0f - t;

            if (dither)
            {
                float d = (s_bayer4x4[(y & 3) * 4 + (x & 3)] - 0.5f) / 255.0f;
                t = Mathf.Clamp(t + d, 0.0f, 1.0f);
            }
            else
            {
                t = Mathf.Clamp(t, 0.0f, 1.0f);
            }

            if (Stops.Count == 0) return Colors.White;
            if (Stops.Count == 1) return Stops[0].Color;

            if (t <= Stops[0].Position) return Stops[0].Color;
            if (t >= Stops[^1].Position) return Stops[^1].Color;

            for (int i = 0; i < Stops.Count - 1; i++)
            {
                var s0 = Stops[i];
                var s1 = Stops[i + 1];

                if (t >= s0.Position && t <= s1.Position)
                {
                    float range = s1.Position - s0.Position;
                    float u = range > 0.00001f ? (t - s0.Position) / range : 0f;

                    // Non-linear midpoint mapping
                    float m = s0.Midpoint;
                    if (MathF.Abs(m - 0.5f) > 0.01f)
                    {
                        u = u < m ? 0.5f * (u / m) : 0.5f + 0.5f * ((u - m) / (1.0f - m));
                    }

                    Color c0 = s0.Color;
                    Color c1 = s1.Color;
                    return new Color(
                        Mathf.Lerp(c0.R, c1.R, u),
                        Mathf.Lerp(c0.G, c1.G, u),
                        Mathf.Lerp(c0.B, c1.B, u),
                        Mathf.Lerp(c0.A, c1.A, u)
                    );
                }
            }

            return Stops[^1].Color;
        }

        public Image GeneratePreviewImage(int width = 256, int height = 28, bool reverse = false)
        {
            var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            for (int x = 0; x < width; x++)
            {
                float t = (float)x / (width - 1);
                Color col = Evaluate(t, reverse: reverse, dither: true, x: x, y: 0);
                for (int y = 0; y < height; y++)
                {
                    img.SetPixel(x, y, col);
                }
            }
            return img;
        }
    }

    public static class PhotoshopGrdParser
    {
        public static List<GradientDefinition> GetBuiltinPresets()
        {
            return new List<GradientDefinition>
            {
                new GradientDefinition("Black to White",
                    (0.00f, new Color(0.0f, 0.0f, 0.0f)),
                    (1.00f, new Color(1.0f, 1.0f, 1.0f))
                ),
                new GradientDefinition("Deadlock Occult Amber",
                    (0.00f, new Color(0.05f, 0.03f, 0.02f)),
                    (0.35f, new Color(0.29f, 0.13f, 0.03f)),
                    (0.70f, new Color(0.83f, 0.49f, 0.12f)),
                    (1.00f, new Color(1.00f, 0.89f, 0.63f))
                ),
                new GradientDefinition("Royal Gold & Bronze",
                    (0.00f, new Color(0.08f, 0.05f, 0.02f)),
                    (0.40f, new Color(0.36f, 0.25f, 0.06f)),
                    (0.75f, new Color(0.83f, 0.65f, 0.23f)),
                    (1.00f, new Color(1.00f, 0.95f, 0.77f))
                ),
                new GradientDefinition("Cyberpunk Neon (Magenta/Cyan)",
                    (0.00f, new Color(0.03f, 0.01f, 0.08f)),
                    (0.35f, new Color(0.29f, 0.05f, 0.27f)),
                    (0.70f, new Color(0.85f, 0.06f, 0.42f)),
                    (1.00f, new Color(0.00f, 0.94f, 1.00f))
                ),
                new GradientDefinition("Crimson Hellfire",
                    (0.00f, new Color(0.06f, 0.01f, 0.01f)),
                    (0.30f, new Color(0.36f, 0.03f, 0.03f)),
                    (0.65f, new Color(0.84f, 0.14f, 0.05f)),
                    (0.90f, new Color(1.00f, 0.57f, 0.00f)),
                    (1.00f, new Color(1.00f, 0.95f, 0.66f))
                ),
                new GradientDefinition("Frostbite Ice",
                    (0.00f, new Color(0.01f, 0.05f, 0.10f)),
                    (0.35f, new Color(0.07f, 0.21f, 0.36f)),
                    (0.70f, new Color(0.31f, 0.63f, 0.86f)),
                    (1.00f, new Color(0.89f, 0.96f, 1.00f))
                ),
                new GradientDefinition("Eldritch Toxic Green",
                    (0.00f, new Color(0.02f, 0.05f, 0.02f)),
                    (0.35f, new Color(0.04f, 0.22f, 0.08f)),
                    (0.70f, new Color(0.20f, 0.70f, 0.29f)),
                    (1.00f, new Color(0.76f, 1.00f, 0.52f))
                ),
                new GradientDefinition("Vintage Sepia",
                    (0.00f, new Color(0.10f, 0.06f, 0.03f)),
                    (0.40f, new Color(0.33f, 0.22f, 0.12f)),
                    (0.75f, new Color(0.69f, 0.55f, 0.36f)),
                    (1.00f, new Color(0.96f, 0.91f, 0.78f))
                ),
                new GradientDefinition("Noir Blue Shadows / Warm Highlights",
                    (0.00f, new Color(0.04f, 0.07f, 0.10f)),
                    (0.40f, new Color(0.11f, 0.17f, 0.24f)),
                    (0.75f, new Color(0.71f, 0.60f, 0.49f)),
                    (1.00f, new Color(1.00f, 0.96f, 0.92f))
                ),
                new GradientDefinition("Ultraviolet Sunset",
                    (0.00f, new Color(0.07f, 0.02f, 0.14f)),
                    (0.35f, new Color(0.33f, 0.07f, 0.37f)),
                    (0.70f, new Color(0.70f, 0.22f, 0.32f)),
                    (1.00f, new Color(0.97f, 0.69f, 0.36f))
                ),
                new GradientDefinition("Copper Verdigris",
                    (0.00f, new Color(0.08f, 0.06f, 0.05f)),
                    (0.35f, new Color(0.28f, 0.17f, 0.11f)),
                    (0.70f, new Color(0.24f, 0.64f, 0.55f)),
                    (1.00f, new Color(0.72f, 0.93f, 0.85f))
                ),
                new GradientDefinition("Synthwave Chrome",
                    (0.00f, new Color(0.03f, 0.01f, 0.10f)),
                    (0.25f, new Color(0.18f, 0.06f, 0.40f)),
                    (0.50f, new Color(0.88f, 0.11f, 0.28f)),
                    (0.75f, new Color(0.02f, 0.71f, 0.83f)),
                    (1.00f, new Color(0.97f, 0.98f, 0.99f))
                )
            };
        }

        public static List<GradientDefinition> LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                GD.PrintErr($"[PhotoshopGrdParser] File not found: {path}");
                return new List<GradientDefinition>();
            }

            try
            {
                byte[] data = File.ReadAllBytes(path);
                string fileName = Path.GetFileNameWithoutExtension(path);
                return Parse(data, fileName);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[PhotoshopGrdParser] Error reading file '{path}': {ex.Message}");
                return new List<GradientDefinition>();
            }
        }

        public static List<GradientDefinition> Parse(byte[] data, string fallbackName = "Gradient")
        {
            var results = new List<GradientDefinition>();
            if (data == null || data.Length < 8) return results;

            // Search for "8BGR" magic
            int magicIndex = -1;
            for (int i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == '8' && data[i + 1] == 'B' && data[i + 2] == 'G' && data[i + 3] == 'R')
                {
                    magicIndex = i;
                    break;
                }
            }

            if (magicIndex < 0)
            {
                GD.PrintErr("[PhotoshopGrdParser] Not a valid Photoshop .grd file (magic '8BGR' not found).");
                return results;
            }

            int offset = magicIndex + 4;
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            offset += 2;

            if (version == 3)
            {
                ParseVersion3(data, offset, fallbackName, results);
            }
            else if (version == 5)
            {
                ParseVersion5(data, offset, fallbackName, results);
            }
            else
            {
                // Fallback attempt with both
                ParseVersion5(data, offset, fallbackName, results);
                if (results.Count == 0)
                {
                    ParseVersion3(data, offset, fallbackName, results);
                }
            }

            return results;
        }

        private static void ParseVersion3(byte[] data, int offset, string fallbackName, List<GradientDefinition> results)
        {
            try
            {
                if (offset + 2 > data.Length) return;
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                offset += 2;

                for (int g = 0; g < count && offset < data.Length - 4; g++)
                {
                    // Pascal string gradient name
                    byte nameLen = data[offset++];
                    string gradName = fallbackName;
                    if (nameLen > 0 && offset + nameLen <= data.Length)
                    {
                        gradName = Encoding.ASCII.GetString(data, offset, nameLen);
                        offset += nameLen;
                    }

                    if (offset + 2 > data.Length) break;
                    ushort numColorStops = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                    offset += 2;

                    var grad = new GradientDefinition { Name = string.IsNullOrWhiteSpace(gradName) ? $"{fallbackName} {g + 1}" : gradName };

                    for (int s = 0; s < numColorStops && offset <= data.Length - 18; s++)
                    {
                        uint loc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                        offset += 4;
                        uint mid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                        offset += 4;
                        ushort colorSpace = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;

                        ushort c0 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;
                        ushort c1 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;
                        ushort c2 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;
                        ushort c3 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;

                        offset += 2; // skip padding

                        float pos = Mathf.Clamp(loc / 4096.0f, 0f, 1f);
                        float midpoint = Mathf.Clamp(mid / 100.0f, 0.01f, 0.99f);

                        Color col;
                        if (colorSpace == 0) // RGB
                        {
                            col = new Color(c0 / 65535f, c1 / 65535f, c2 / 65535f, 1f);
                        }
                        else if (colorSpace == 7) // Grayscale
                        {
                            float gray = c0 / 10000.0f;
                            col = new Color(gray, gray, gray, 1f);
                        }
                        else if (colorSpace == 1) // HSB
                        {
                            col = Color.FromHsv(c0 / 65535f, c1 / 65535f, c2 / 65535f, 1f);
                        }
                        else
                        {
                            col = new Color(c0 / 65535f, c1 / 65535f, c2 / 65535f, 1f);
                        }

                        grad.Stops.Add(new GradientStop(pos, col, midpoint));
                    }

                    // Transparency stops
                    if (offset + 2 <= data.Length)
                    {
                        ushort numTransStops = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                        offset += 2;

                        for (int t = 0; t < numTransStops && offset <= data.Length - 10; t++)
                        {
                            uint loc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                            offset += 4;
                            uint mid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                            offset += 4;
                            ushort opacity = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                            offset += 2;

                            float pos = Mathf.Clamp(loc / 4096.0f, 0f, 1f);
                            float op = Mathf.Clamp(opacity / 255.0f, 0f, 1f);
                            grad.TransparencyStops.Add(new GradientTransparencyStop(pos, op, mid / 100.0f));
                        }
                    }

                    if (grad.Stops.Count > 0)
                    {
                        grad.Stops.Sort((a, b) => a.Position.CompareTo(b.Position));
                        results.Add(grad);
                    }
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[PhotoshopGrdParser] Warning during v3 parse: {ex.Message}");
            }
        }

        private static void ParseVersion5(byte[] data, int startOffset, string fallbackName, List<GradientDefinition> results)
        {
            // Robust ActionDescriptor scanner for "Grdn" entries
            try
            {
                ReadOnlySpan<byte> span = data.AsSpan();
                int pos = startOffset;

                while (pos <= span.Length - 8)
                {
                    // Search for "Grdn"
                    if (span[pos] == 'G' && span[pos + 1] == 'r' && span[pos + 2] == 'd' && span[pos + 3] == 'n')
                    {
                        int grdnStart = pos;
                        pos += 4;

                        // Scan next 4096 bytes for gradient details
                        int windowEnd = Math.Min(span.Length, grdnStart + 8192);
                        var grad = ExtractGradientFromWindow(span.Slice(grdnStart, windowEnd - grdnStart), fallbackName);
                        if (grad != null && grad.Stops.Count >= 2)
                        {
                            results.Add(grad);
                        }
                    }
                    else
                    {
                        pos++;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[PhotoshopGrdParser] Warning during v5 parse: {ex.Message}");
            }
        }

        private static GradientDefinition ExtractGradientFromWindow(ReadOnlySpan<byte> window, string fallbackName)
        {
            try
            {
                string gradName = fallbackName;

                // 1. Look for name "Nm  "
                int nmIdx = FindPattern(window, "Nm  ");
                if (nmIdx >= 0 && nmIdx + 8 < window.Length)
                {
                    int nameOffset = nmIdx + 4;
                    // Check if followed by TEXT tag
                    if (nameOffset + 4 < window.Length && window[nameOffset] == 'T' && window[nameOffset + 1] == 'E' && window[nameOffset + 2] == 'X' && window[nameOffset + 3] == 'T')
                    {
                        nameOffset += 4;
                    }
                    if (nameOffset + 4 <= window.Length)
                    {
                        int charCount = BinaryPrimitives.ReadInt32BigEndian(window.Slice(nameOffset, 4));
                        nameOffset += 4;
                        if (charCount > 0 && charCount < 256 && nameOffset + charCount * 2 <= window.Length)
                        {
                            gradName = Encoding.BigEndianUnicode.GetString(window.Slice(nameOffset, charCount * 2)).Trim('\0');
                        }
                    }
                }

                var grad = new GradientDefinition { Name = string.IsNullOrWhiteSpace(gradName) ? fallbackName : gradName };

                // 2. Scan for color stops "Clrt"
                int p = 0;
                while (p <= window.Length - 16)
                {
                    if (window[p] == 'C' && window[p + 1] == 'l' && window[p + 2] == 'r' && window[p + 3] == 't')
                    {
                        int clrtStart = p;
                        int clrtEnd = Math.Min(window.Length, clrtStart + 512);
                        var stopSpan = window.Slice(clrtStart, clrtEnd - clrtStart);

                        // Find "Lctn" (Location)
                        float pos = 0f;
                        int lctnIdx = FindPattern(stopSpan, "Lctn");
                        if (lctnIdx >= 0 && lctnIdx + 8 <= stopSpan.Length)
                        {
                            int valOffset = lctnIdx + 4;
                            if (valOffset + 4 <= stopSpan.Length && stopSpan[valOffset] == 'l' && stopSpan[valOffset + 1] == 'o' && stopSpan[valOffset + 2] == 'n' && stopSpan[valOffset + 3] == 'g')
                            {
                                valOffset += 4;
                            }
                            if (valOffset + 4 <= stopSpan.Length)
                            {
                                int loc = BinaryPrimitives.ReadInt32BigEndian(stopSpan.Slice(valOffset, 4));
                                pos = Mathf.Clamp(loc / 4096.0f, 0f, 1f);
                            }
                        }

                        // Find "Mdpn" (Midpoint)
                        float mid = 0.5f;
                        int mdpnIdx = FindPattern(stopSpan, "Mdpn");
                        if (mdpnIdx >= 0 && mdpnIdx + 8 <= stopSpan.Length)
                        {
                            int valOffset = mdpnIdx + 4;
                            if (valOffset + 4 <= stopSpan.Length && stopSpan[valOffset] == 'l' && stopSpan[valOffset + 1] == 'o' && stopSpan[valOffset + 2] == 'n' && stopSpan[valOffset + 3] == 'g')
                            {
                                valOffset += 4;
                            }
                            if (valOffset + 4 <= stopSpan.Length)
                            {
                                int m = BinaryPrimitives.ReadInt32BigEndian(stopSpan.Slice(valOffset, 4));
                                mid = Mathf.Clamp(m / 100.0f, 0.01f, 0.99f);
                            }
                        }

                        // Find Color "RGBC" or "HSBC" or "Grsc"
                        Color col = Colors.White;
                        int rgbIdx = FindPattern(stopSpan, "RGBC");
                        if (rgbIdx >= 0)
                        {
                            col = ExtractRgbDoubles(stopSpan.Slice(rgbIdx));
                        }
                        else
                        {
                            int hsbIdx = FindPattern(stopSpan, "HSBC");
                            if (hsbIdx >= 0)
                            {
                                col = ExtractHsbDoubles(stopSpan.Slice(hsbIdx));
                            }
                        }

                        grad.Stops.Add(new GradientStop(pos, col, mid));
                        p += 20;
                    }
                    else
                    {
                        p++;
                    }
                }

                if (grad.Stops.Count >= 2)
                {
                    grad.Stops.Sort((a, b) => a.Position.CompareTo(b.Position));
                    return grad;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static Color ExtractRgbDoubles(ReadOnlySpan<byte> span)
        {
            float r = 1f, g = 1f, b = 1f;
            int rIdx = FindPattern(span, "Rd  ");
            if (rIdx >= 0) r = ReadDescriptorDouble(span.Slice(rIdx + 4)) / 255.0f;

            int gIdx = FindPattern(span, "Grn ");
            if (gIdx >= 0) g = ReadDescriptorDouble(span.Slice(gIdx + 4)) / 255.0f;

            int bIdx = FindPattern(span, "Bl  ");
            if (bIdx >= 0) b = ReadDescriptorDouble(span.Slice(bIdx + 4)) / 255.0f;

            return new Color(Mathf.Clamp(r, 0f, 1f), Mathf.Clamp(g, 0f, 1f), Mathf.Clamp(b, 0f, 1f), 1f);
        }

        private static Color ExtractHsbDoubles(ReadOnlySpan<byte> span)
        {
            float h = 0f, s = 0f, b = 1f;
            int hIdx = FindPattern(span, "H   ");
            if (hIdx >= 0) h = ReadDescriptorDouble(span.Slice(hIdx + 4)) / 360.0f;

            int sIdx = FindPattern(span, "Strt");
            if (sIdx >= 0) s = ReadDescriptorDouble(span.Slice(sIdx + 4)) / 100.0f;

            int bIdx = FindPattern(span, "Brgh");
            if (bIdx >= 0) b = ReadDescriptorDouble(span.Slice(bIdx + 4)) / 100.0f;

            return Color.FromHsv(Mathf.Clamp(h, 0f, 1f), Mathf.Clamp(s, 0f, 1f), Mathf.Clamp(b, 0f, 1f), 1f);
        }

        private static float ReadDescriptorDouble(ReadOnlySpan<byte> span)
        {
            if (span.Length < 8) return 0f;
            int off = 0;
            if (span.Length >= 12 && span[0] == 'd' && span[1] == 'o' && span[2] == 'u' && span[3] == 'b')
            {
                off = 4;
            }
            if (off + 8 <= span.Length)
            {
                double d = BinaryPrimitives.ReadDoubleBigEndian(span.Slice(off, 8));
                return (float)d;
            }
            return 0f;
        }

        private static int FindPattern(ReadOnlySpan<byte> data, string asciiPattern)
        {
            byte[] patternBytes = Encoding.ASCII.GetBytes(asciiPattern);
            for (int i = 0; i <= data.Length - patternBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < patternBytes.Length; j++)
                {
                    if (data[i + j] != patternBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        public static string GetUserGradientsDirectory()
        {
            string globalPath = ProjectSettings.GlobalizePath("user://gradients");
            if (!Directory.Exists(globalPath))
            {
                Directory.CreateDirectory(globalPath);
            }
            return globalPath;
        }

        public static List<GradientDefinition> LoadUserGradients()
        {
            var list = new List<GradientDefinition>();
            string dir = GetUserGradientsDirectory();
            if (!Directory.Exists(dir)) return list;

            string[] files = Directory.GetFiles(dir, "*.grd");
            foreach (var f in files)
            {
                var imported = LoadFromFile(f);
                list.AddRange(imported);
            }
            return list;
        }

        public static void SaveUserGradientFile(string sourceFilePath, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            string dir = GetUserGradientsDirectory();
            string fileName = Path.GetFileName(sourceFilePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "imported.grd";
            string targetPath = Path.Combine(dir, fileName);

            File.WriteAllBytes(targetPath, data);
            GD.Print($"[PhotoshopGrdParser] Saved gradient file to user storage: {targetPath}");
        }
    }
}
