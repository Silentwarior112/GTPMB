namespace GTPMB.Core.Textures.Tex1;

/// <summary>
/// PS2 "Tex1" texture set (TextureSet1) codec - the in-process replacement for
/// "GTPS2ModelTool dump -i x.img" and "GTPS2ModelTool make-tex-set -i x.png".
/// Ported from PDTools.Files/Textures/PS2 (TextureSet1, TextureSetPS2Base, TextureSetBuilder, GSMemory,
/// GSPixelFormats, Tex1Utils, ...), trimmed to what a GPB menu texture needs.
/// </summary>
public static class Tex1Codec
{
    public static bool IsTex1(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 'T' && data[1] == 'e' && data[2] == 'x' && data[3] == '1';

    /// <summary>Number of pglu textures in the set (a set only round-trips as a single PNG when this is 1).</summary>
    public static int GetTextureCount(byte[] tex1) => Tex1Reader.ReadTextureCount(tex1);

    /// <summary>Decodes texture <paramref name="index"/> to RGBA, cropped to its real size (GS alpha 0x80 -> 255).</summary>
    public static RgbaImage DecodeTexture(byte[] tex1, int index = 0) => new Tex1Reader(tex1).Decode(index);

    /// <summary>
    /// Builds a single-texture Tex1 file, picking the GS pixel format purely from the image's distinct
    /// RGBA colour count: up to 16 -> PSMT4, up to 256 -> PSMT8, more -> PSMCT32 (32 bpp true colour).
    /// Always lossless: this tool NEVER quantizes (the user reduces colours beforehand if they want an
    /// indexed format).
    /// </summary>
    public static byte[] Build(RgbaImage image) => Tex1Builder.Build(image);
}
