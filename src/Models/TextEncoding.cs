using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SourceGit.Models
{
    /// <summary>
    ///     An encoding that can be used to decode the content of a text diff.
    ///
    ///     Only ASCII-compatible encodings are listed here. The patch parser splits the raw output of
    ///     `git diff` on '\n' bytes and reads the first byte of each line as its type marker, so the
    ///     encodings that may use those bytes inside a character (UTF-16, UTF-32) can not be used.
    /// </summary>
    public class TextEncoding
    {
        static TextEncoding()
        {
            // Only a handful of encodings are built into .NET. The legacy code pages used below are
            // provided by this extra provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static TextEncoding Auto { get; } = new TextEncoding("Auto", 0);
        public static TextEncoding UTF8 { get; } = new TextEncoding("UTF-8", 65001);
        public static TextEncoding GB18030 { get; } = new TextEncoding("GB18030", 54936);
        public static TextEncoding GBK { get; } = new TextEncoding("GBK", 936);
        public static TextEncoding Big5 { get; } = new TextEncoding("Big5", 950);
        public static TextEncoding ShiftJIS { get; } = new TextEncoding("Shift-JIS", 932);
        public static TextEncoding EUCKR { get; } = new TextEncoding("EUC-KR", 949);
        public static TextEncoding Windows1252 { get; } = new TextEncoding("Windows-1252", 1252);
        public static TextEncoding Latin1 { get; } = new TextEncoding("ISO-8859-1", 28591);

        public static List<TextEncoding> Supported { get; } =
        [
            Auto,
            UTF8,
            GB18030,
            GBK,
            Big5,
            ShiftJIS,
            EUCKR,
            Windows1252,
            Latin1,
        ];

        /// <summary>
        ///     The encoding for the ANSI code page of the running system, or null if it is not one of
        ///     the supported encodings.
        /// </summary>
        public static TextEncoding SystemDefault { get; } = Find(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

        public string Name { get; }

        /// <summary>
        ///     Zero means the encoding should be guessed from the content of the diff.
        /// </summary>
        public int CodePage { get; }

        public bool IsAuto => CodePage == 0;

        public Encoding Decoder => _decoder ??= CreateDecoder();

        public static TextEncoding Find(int codePage)
        {
            foreach (var one in Supported)
            {
                if (!one.IsAuto && one.CodePage == codePage)
                    return one;
            }

            return null;
        }

        private TextEncoding(string name, int codePage)
        {
            Name = name;
            CodePage = codePage;
        }

        private Encoding CreateDecoder()
        {
            if (IsAuto)
                return Encoding.UTF8;

            try
            {
                return Encoding.GetEncoding(CodePage);
            }
            catch
            {
                // The code page may be unavailable on this platform. Showing the diff decoded as
                // UTF-8 is better than showing nothing at all.
                return Encoding.UTF8;
            }
        }

        private Encoding _decoder = null;
    }
}
