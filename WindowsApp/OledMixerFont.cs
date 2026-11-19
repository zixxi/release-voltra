namespace Voltra;

internal static class OledMixerFont
{
    // Printable ASCII from the public-domain Misc Fixed 5×7 bitmap font,
    // rasterized from u8g2_font_5x7_tr. Each column occupies seven bits.
    private static readonly ulong[] Glyphs =
    [
        0x000000000, 0x0000BC000, 0x000E00380, 0x147C51F14, 0x1054F9504,
        0x006410413, 0x004051514, 0x00001C000, 0x000084F00, 0x000079080,
        0x005471500, 0x0810F8408, 0x0020C2000, 0x001020408, 0x0000C1800,
        0x000410410, 0x003C84F00, 0x0040FD100, 0x004CA58A2, 0x003695291,
        0x0010FC50C, 0x003295297, 0x00309529E, 0x000635881, 0x00349529A,
        0x003CA5486, 0x0000D9B00, 0x000059B40, 0x004450400, 0x002850A14,
        0x001051100, 0x000CA4100, 0x001CB509E, 0x007C244BE, 0x0034952BF,
        0x00248509E, 0x003C850BF, 0x0042952BF, 0x0002142BF, 0x0074A509E,
        0x007E1023F, 0x0042FD080, 0x003E81010, 0x00424863F, 0x00408103F,
        0x007E1833F, 0x007E6033F, 0x003C8509E, 0x000C244BF, 0x00BC8589E,
        0x004C644BF, 0x0024A5292, 0x0002FC080, 0x003E8101F, 0x001EC180F,
        0x007E60C3F, 0x006630633, 0x000EE0380, 0x0046954B1, 0x004285F80,
        0x002020202, 0x007E85080, 0x000404100, 0x004081020, 0x000008080,
        0x007851218, 0x00309123F, 0x000091218, 0x007E91218, 0x0010B1A18,
        0x000425F08, 0x009952A28, 0x00701023F, 0x0040F5200, 0x007B01000,
        0x00405043F, 0x0040FD080, 0x00703043C, 0x00701023C, 0x003091218,
        0x00309127C, 0x00F891218, 0x00101023C, 0x0028D1628, 0x004090F84,
        0x00788101C, 0x003880E00, 0x0078C183C, 0x004860C24, 0x00388280C,
        0x0048B1A24, 0x004278200, 0x0000FC000, 0x000879080, 0x000208082,
    ];

    private static ulong Glyph(char character) => Glyphs[character is >= ' ' and <= '~' ? character - ' ' : '?' - ' '];

    internal static int Measure(string text)
    {
        if (text.Length == 0) return 0;
        ulong last = Glyph(text[^1]);
        int width = (System.Numerics.BitOperations.Log2(last | 1) + 7) / 7;
        return (text.Length - 1) * 5 + width;
    }

    internal static void Draw(byte[] frame, string text, int x, int y)
    {
        foreach (char character in text)
        {
            ulong glyph = Glyph(character);
            for (int column = 0; column < 5; column++)
                for (int row = 0; row < 7; row++)
                    if ((glyph & (1UL << (column * 7 + row))) != 0)
                        Pixel(frame, x + column, y + row);
            x += 5;
        }
    }

    internal static void Pixel(byte[] frame, int x, int y) =>
        frame[y * 16 + x / 8] |= (byte)(0x80 >> (x & 7));
}
