using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GunsawImageCode;

public enum ArtObjectKind : byte
{
    Tile = 0,
    InfoScreen = 1,
    Background = 2,
    Ice = 3
}

public sealed class ArtObject
{
    public ArtObjectKind Kind { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float Rotation { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class ArtDocument
{
    public string Mode { get; set; } = "contour";
    public List<ArtObject> Objects { get; } = new List<ArtObject>();
    public int UnoptimizedObjectCount { get; set; }
}

public static class ArtCode
{
    public const string Prefix = "GSI1:";
    private const int MaximumObjects = 100_000;

    public static string Encode(ArtDocument document)
    {
        if (document == null)
            throw new ArgumentNullException(nameof(document));
        if (document.Objects.Count > MaximumObjects)
            throw new InvalidDataException("Too many objects.");

        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)2);
            writer.Write(document.Mode ?? string.Empty);
            writer.Write(document.Objects.Count);
            foreach (var item in document.Objects)
            {
                Validate(item);
                writer.Write((byte)item.Kind);
                writer.Write(item.X);
                writer.Write(item.Y);
                writer.Write(item.Width);
                writer.Write(item.Height);
                writer.Write(item.Rotation);
                writer.Write(item.Text ?? string.Empty);
            }
        }

        raw.Position = 0;
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            raw.CopyTo(deflate);
        return Prefix + Convert.ToBase64String(packed.ToArray());
    }

    public static ArtDocument Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.Trim().StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Clipboard does not contain a Gunsaw image code.");

        var base64 = code.Trim().Substring(Prefix.Length);
        var bytes = Convert.FromBase64String(base64);
        using var input = new MemoryStream(bytes, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        deflate.CopyTo(raw);
        raw.Position = 0;

        using var reader = new BinaryReader(raw, Encoding.UTF8, leaveOpen: false);
        var version = reader.ReadByte();
        if (version != 1 && version != 2)
            throw new InvalidDataException("Unsupported image code version.");

        var result = new ArtDocument { Mode = reader.ReadString() };
        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumObjects)
            throw new InvalidDataException("Invalid object count.");

        for (var i = 0; i < count; i++)
        {
            var item = new ArtObject
            {
                Kind = (ArtObjectKind)reader.ReadByte(),
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Width = reader.ReadSingle(),
                Height = reader.ReadSingle(),
                Rotation = reader.ReadSingle(),
                Text = version >= 2 ? reader.ReadString() : string.Empty
            };
            Validate(item);
            result.Objects.Add(item);
        }

        if (raw.Position != raw.Length)
            throw new InvalidDataException("Image code has trailing data.");
        return result;
    }

    private static void Validate(ArtObject item)
    {
        if (item.Kind != ArtObjectKind.Tile && item.Kind != ArtObjectKind.InfoScreen &&
            item.Kind != ArtObjectKind.Background && item.Kind != ArtObjectKind.Ice)
            throw new InvalidDataException("Unknown object type.");
        if (!Finite(item.X) || !Finite(item.Y) || !Finite(item.Width) || !Finite(item.Height) || !Finite(item.Rotation))
            throw new InvalidDataException("Object contains an invalid number.");
        if (item.Width <= 0f || item.Height <= 0f || item.Width > 500f || item.Height > 500f)
            throw new InvalidDataException("Object has an invalid size.");
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}