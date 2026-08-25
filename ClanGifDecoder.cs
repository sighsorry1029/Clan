using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clan;

/// <summary>
/// Decodes bounded GIF87a/GIF89a data without native image libraries.
/// Output frames are fully composited, top-to-bottom RGBA32 canvases.
/// </summary>
internal static class ClanGifDecoder
{
    private const int MaximumLzwCodeCount = 4096;

    private enum DisposalMethod
    {
        NotSpecified = 0,
        DoNotDispose = 1,
        RestoreToBackground = 2,
        RestoreToPrevious = 3
    }

    internal sealed class Frame
    {
        internal Frame(
            int delayMilliseconds,
            bool requiresUserInput,
            byte[] rgba32)
        {
            DelayMilliseconds = delayMilliseconds;
            RequiresUserInput = requiresUserInput;
            Rgba32 = rgba32;
        }

        public int DelayMilliseconds { get; }

        public bool RequiresUserInput { get; }

        /// <summary>
        /// Fully composited canvas pixels in top-to-bottom, left-to-right
        /// R, G, B, A byte order. The array is owned by this frame.
        /// </summary>
        public byte[] Rgba32 { get; }
    }

    internal sealed class Animation
    {
        internal Animation(
            int width,
            int height,
            bool loopsForever,
            List<Frame> frames)
        {
            Width = width;
            Height = height;
            LoopsForever = loopsForever;
            Frames = frames.AsReadOnly();
        }

        public int Width { get; }

        public int Height { get; }

        public bool LoopsForever { get; }

        public IReadOnlyList<Frame> Frames { get; }
    }

    /// <summary>
    /// Decodes a GIF into fully composited RGBA32 frames.
    /// </summary>
    /// <param name="data">Complete GIF87a or GIF89a file bytes.</param>
    /// <param name="maximumBytes">Maximum accepted encoded file size.</param>
    /// <param name="maximumDimension">
    /// Maximum accepted logical-screen width and height.
    /// </param>
    /// <param name="maximumFrames">Maximum accepted image-frame count.</param>
    /// <param name="maximumDecodedPixels">
    /// Maximum sum of full composited canvas pixels across returned frames.
    /// For example, 20 frames at 200x200 consume 800,000 decoded pixels.
    /// </param>
    /// <returns>
    /// An animation whose frame byte arrays are independent full canvases.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The limits are invalid, a limit is exceeded, or the GIF structure,
    /// palette, LZW stream, frame rectangle, or trailer is malformed.
    /// </exception>
    public static Animation Decode(
        byte[] data,
        int maximumBytes,
        int maximumDimension,
        int maximumFrames,
        long maximumDecodedPixels)
    {
        if (data == null)
        {
            throw new InvalidDataException("GIF data is required.");
        }
        if (maximumBytes <= 0 ||
            maximumDimension <= 0 ||
            maximumFrames <= 0 ||
            maximumDecodedPixels <= 0)
        {
            throw new InvalidDataException("GIF decode limits must be positive.");
        }
        if (data.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"GIF size {data.Length} exceeds the {maximumBytes}-byte limit.");
        }

        return new Decoder(
            data,
            maximumDimension,
            maximumFrames,
            maximumDecodedPixels).Decode();
    }

    private sealed class Decoder
    {
        private readonly Reader _reader;
        private readonly int _maximumDimension;
        private readonly int _maximumFrames;
        private readonly long _maximumDecodedPixels;
        private readonly List<Frame> _frames = new();

        private int _width;
        private int _height;
        private int _backgroundColorIndex;
        private long _canvasPixels;
        private byte[]? _globalColorTable;
        private byte[] _canvas = Array.Empty<byte>();
        private bool _canvasInitialized;
        private int? _loopCount;

        private bool _hasPendingControl;
        private GraphicControl _pendingControl;

        private bool _hasPreviousFrame;
        private DisposalMethod _previousDisposal;
        private int _previousLeft;
        private int _previousTop;
        private int _previousWidth;
        private int _previousHeight;
        private bool _previousHadTransparency;
        private int _previousTransparentColorIndex;
        private byte[]? _previousRestoreCanvas;

        public Decoder(
            byte[] data,
            int maximumDimension,
            int maximumFrames,
            long maximumDecodedPixels)
        {
            _reader = new Reader(data);
            _maximumDimension = maximumDimension;
            _maximumFrames = maximumFrames;
            _maximumDecodedPixels = maximumDecodedPixels;
        }

        public Animation Decode()
        {
            ReadLogicalScreen();

            while (true)
            {
                byte introducer = _reader.ReadByte("GIF block introducer");
                switch (introducer)
                {
                    case 0x2c:
                        ReadImage();
                        break;
                    case 0x21:
                        ReadExtension();
                        break;
                    case 0x3b:
                        return Finish();
                    default:
                        throw new InvalidDataException(
                            $"GIF contains unsupported block introducer 0x{introducer:x2}.");
                }
            }
        }

        private void ReadLogicalScreen()
        {
            string version = _reader.ReadAscii(6, "GIF signature");
            if (!StringComparer.Ordinal.Equals(version, "GIF87a") &&
                !StringComparer.Ordinal.Equals(version, "GIF89a"))
            {
                throw new InvalidDataException("File is not a GIF87a or GIF89a image.");
            }

            _width = _reader.ReadUInt16("logical-screen width");
            _height = _reader.ReadUInt16("logical-screen height");
            if (_width <= 0 ||
                _height <= 0 ||
                _width > _maximumDimension ||
                _height > _maximumDimension)
            {
                throw new InvalidDataException(
                    $"GIF canvas must be 1-{_maximumDimension}px in each dimension.");
            }

            _canvasPixels = (long)_width * _height;
            if (_canvasPixels > _maximumDecodedPixels ||
                _canvasPixels > int.MaxValue / 4)
            {
                throw new InvalidDataException("GIF canvas exceeds the decoded-pixel limit.");
            }

            byte packed = _reader.ReadByte("logical-screen flags");
            _backgroundColorIndex = _reader.ReadByte("background color index");
            _reader.ReadByte("pixel aspect ratio");

            bool hasGlobalColorTable = (packed & 0x80) != 0;
            if (hasGlobalColorTable)
            {
                int entryCount = 1 << ((packed & 0x07) + 1);
                _globalColorTable = _reader.ReadColorTable(
                    entryCount,
                    "global color table");
                if (_backgroundColorIndex >= entryCount)
                {
                    throw new InvalidDataException(
                        "GIF background color index is outside the global color table.");
                }
            }

            _canvas = new byte[(int)_canvasPixels * 4];
        }

        private void ReadExtension()
        {
            byte label = _reader.ReadByte("GIF extension label");
            switch (label)
            {
                case 0xf9:
                    ReadGraphicControl();
                    break;
                case 0xff:
                    ReadApplicationExtension();
                    break;
                case 0xfe:
                    _reader.SkipSubBlocks("GIF comment extension");
                    break;
                case 0x01:
                    throw new InvalidDataException(
                        "GIF Plain Text Extension rendering is not supported.");
                default:
                    _reader.SkipSubBlocks(
                        $"GIF extension 0x{label:x2}");
                    break;
            }
        }

        private void ReadGraphicControl()
        {
            if (_hasPendingControl)
            {
                throw new InvalidDataException(
                    "GIF contains multiple graphic controls for one rendering block.");
            }

            int blockSize = _reader.ReadByte("graphic-control block size");
            if (blockSize != 4)
            {
                throw new InvalidDataException(
                    "GIF graphic-control block must contain four bytes.");
            }

            byte packed = _reader.ReadByte("graphic-control flags");
            if ((packed & 0xe0) != 0)
            {
                throw new InvalidDataException(
                    "GIF graphic-control block has nonzero reserved bits.");
            }

            int disposalValue = (packed >> 2) & 0x07;
            if (disposalValue > (int)DisposalMethod.RestoreToPrevious)
            {
                throw new InvalidDataException(
                    $"GIF uses unsupported disposal method {disposalValue}.");
            }

            int delay = _reader.ReadUInt16("frame delay");
            int transparentColorIndex = _reader.ReadByte("transparent color index");
            if (_reader.ReadByte("graphic-control terminator") != 0)
            {
                throw new InvalidDataException(
                    "GIF graphic-control block has an invalid terminator.");
            }

            _pendingControl = new GraphicControl(
                (DisposalMethod)disposalValue,
                (packed & 0x02) != 0,
                (packed & 0x01) != 0,
                transparentColorIndex,
                delay);
            _hasPendingControl = true;
        }

        private void ReadApplicationExtension()
        {
            int blockSize = _reader.ReadByte("application-extension block size");
            if (blockSize != 11)
            {
                throw new InvalidDataException(
                    "GIF application identifier must contain eleven bytes.");
            }

            string identifier = _reader.ReadAscii(
                blockSize,
                "application identifier");
            bool isLoopExtension =
                StringComparer.Ordinal.Equals(identifier, "NETSCAPE2.0") ||
                StringComparer.Ordinal.Equals(identifier, "ANIMEXTS1.0");

            if (!isLoopExtension)
            {
                _reader.SkipSubBlocks($"GIF application '{identifier}'");
                return;
            }

            int loopBlockSize = _reader.ReadByte("loop-extension block size");
            if (loopBlockSize != 3 ||
                _reader.ReadByte("loop-extension identifier") != 1)
            {
                throw new InvalidDataException(
                    "GIF loop extension has an invalid data block.");
            }

            int loopCount = _reader.ReadUInt16("loop count");
            if (_reader.ReadByte("loop-extension terminator") != 0)
            {
                throw new InvalidDataException(
                    "GIF loop extension has unexpected extra data.");
            }
            if (_loopCount.HasValue && _loopCount.Value != loopCount)
            {
                throw new InvalidDataException(
                    "GIF contains conflicting loop extensions.");
            }
            _loopCount = loopCount;
        }

        private void ReadImage()
        {
            if (_frames.Count >= _maximumFrames)
            {
                throw new InvalidDataException(
                    $"GIF contains more than {_maximumFrames} frames.");
            }

            int nextFrameCount = _frames.Count + 1;
            if (_canvasPixels > _maximumDecodedPixels / nextFrameCount)
            {
                throw new InvalidDataException(
                    "GIF frames exceed the cumulative decoded-pixel limit.");
            }

            int left = _reader.ReadUInt16("image left");
            int top = _reader.ReadUInt16("image top");
            int width = _reader.ReadUInt16("image width");
            int height = _reader.ReadUInt16("image height");
            if (width <= 0 ||
                height <= 0 ||
                (long)left + width > _width ||
                (long)top + height > _height)
            {
                throw new InvalidDataException(
                    "GIF image rectangle is empty or outside the logical screen.");
            }

            byte packed = _reader.ReadByte("image flags");
            if ((packed & 0x18) != 0)
            {
                throw new InvalidDataException(
                    "GIF image descriptor has nonzero reserved bits.");
            }

            bool hasLocalColorTable = (packed & 0x80) != 0;
            bool interlaced = (packed & 0x40) != 0;
            byte[]? colorTable = _globalColorTable;
            if (hasLocalColorTable)
            {
                int entryCount = 1 << ((packed & 0x07) + 1);
                colorTable = _reader.ReadColorTable(
                    entryCount,
                    "local color table");
            }
            if (colorTable == null)
            {
                throw new InvalidDataException(
                    "GIF image does not have an active color table.");
            }

            GraphicControl control = _hasPendingControl
                ? _pendingControl
                : GraphicControl.Default;
            _hasPendingControl = false;
            _pendingControl = default;

            int paletteEntries = colorTable.Length / 3;
            if (control.HasTransparency &&
                control.TransparentColorIndex >= paletteEntries)
            {
                throw new InvalidDataException(
                    "GIF transparent color index is outside the active color table.");
            }

            int minimumCodeSize = _reader.ReadByte("LZW minimum code size");
            if (minimumCodeSize < 2 || minimumCodeSize > 8)
            {
                throw new InvalidDataException(
                    "GIF LZW minimum code size must be between 2 and 8.");
            }

            long framePixelCountLong = (long)width * height;
            if (framePixelCountLong > int.MaxValue)
            {
                throw new InvalidDataException("GIF image rectangle is too large.");
            }

            byte[] indices = DecodeLzw(
                _reader,
                minimumCodeSize,
                (int)framePixelCountLong);

            EnsureCanvasInitialized(control);
            ApplyPreviousDisposal();
            byte[]? restoreCanvas = control.Disposal == DisposalMethod.RestoreToPrevious
                ? (byte[])_canvas.Clone()
                : null;

            DrawIndices(
                indices,
                colorTable,
                control,
                left,
                top,
                width,
                height,
                interlaced);

            byte[] composited = (byte[])_canvas.Clone();
            _frames.Add(new Frame(
                control.DelayCentiseconds * 10,
                control.RequiresUserInput,
                composited));

            _hasPreviousFrame = true;
            _previousDisposal = control.Disposal;
            _previousLeft = left;
            _previousTop = top;
            _previousWidth = width;
            _previousHeight = height;
            _previousHadTransparency = control.HasTransparency;
            _previousTransparentColorIndex = control.TransparentColorIndex;
            _previousRestoreCanvas = restoreCanvas;
        }

        private void EnsureCanvasInitialized(GraphicControl firstControl)
        {
            if (_canvasInitialized)
            {
                return;
            }

            if (_globalColorTable != null)
            {
                int paletteOffset = _backgroundColorIndex * 3;
                byte alpha =
                    firstControl.HasTransparency &&
                    firstControl.TransparentColorIndex == _backgroundColorIndex
                        ? (byte)0
                        : (byte)255;
                FillCanvas(
                    _globalColorTable[paletteOffset],
                    _globalColorTable[paletteOffset + 1],
                    _globalColorTable[paletteOffset + 2],
                    alpha);
            }

            _canvasInitialized = true;
        }

        private void FillCanvas(byte red, byte green, byte blue, byte alpha)
        {
            int destination = 0;
            while (destination < _canvas.Length)
            {
                _canvas[destination++] = red;
                _canvas[destination++] = green;
                _canvas[destination++] = blue;
                _canvas[destination++] = alpha;
            }
        }

        private void ApplyPreviousDisposal()
        {
            if (!_hasPreviousFrame)
            {
                return;
            }

            switch (_previousDisposal)
            {
                case DisposalMethod.NotSpecified:
                case DisposalMethod.DoNotDispose:
                    break;
                case DisposalMethod.RestoreToBackground:
                    RestorePreviousRectangleToBackground();
                    break;
                case DisposalMethod.RestoreToPrevious:
                    if (_previousRestoreCanvas == null ||
                        _previousRestoreCanvas.Length != _canvas.Length)
                    {
                        throw new InvalidDataException(
                            "GIF restore-to-previous state is unavailable.");
                    }
                    Buffer.BlockCopy(
                        _previousRestoreCanvas,
                        0,
                        _canvas,
                        0,
                        _canvas.Length);
                    break;
                default:
                    throw new InvalidDataException(
                        "GIF contains an invalid previous-frame disposal method.");
            }

            _previousRestoreCanvas = null;
        }

        private void RestorePreviousRectangleToBackground()
        {
            byte red = 0;
            byte green = 0;
            byte blue = 0;
            byte alpha = 0;

            if (_globalColorTable != null)
            {
                int paletteOffset = _backgroundColorIndex * 3;
                red = _globalColorTable[paletteOffset];
                green = _globalColorTable[paletteOffset + 1];
                blue = _globalColorTable[paletteOffset + 2];
                alpha =
                    _previousHadTransparency &&
                    _previousTransparentColorIndex == _backgroundColorIndex
                        ? (byte)0
                        : (byte)255;
            }

            for (int y = 0; y < _previousHeight; y++)
            {
                int destination =
                    ((_previousTop + y) * _width + _previousLeft) * 4;
                for (int x = 0; x < _previousWidth; x++)
                {
                    _canvas[destination++] = red;
                    _canvas[destination++] = green;
                    _canvas[destination++] = blue;
                    _canvas[destination++] = alpha;
                }
            }
        }

        private void DrawIndices(
            byte[] indices,
            byte[] colorTable,
            GraphicControl control,
            int left,
            int top,
            int width,
            int height,
            bool interlaced)
        {
            int sourceOffset = 0;
            if (!interlaced)
            {
                for (int row = 0; row < height; row++)
                {
                    DrawRow(
                        indices,
                        ref sourceOffset,
                        colorTable,
                        control,
                        left,
                        top + row,
                        width);
                }
            }
            else
            {
                int[] starts = { 0, 4, 2, 1 };
                int[] steps = { 8, 8, 4, 2 };
                for (int pass = 0; pass < starts.Length; pass++)
                {
                    for (int row = starts[pass]; row < height; row += steps[pass])
                    {
                        DrawRow(
                            indices,
                            ref sourceOffset,
                            colorTable,
                            control,
                            left,
                            top + row,
                            width);
                    }
                }
            }

            if (sourceOffset != indices.Length)
            {
                throw new InvalidDataException(
                    "GIF interlace pass did not consume the decoded image.");
            }
        }

        private void DrawRow(
            byte[] indices,
            ref int sourceOffset,
            byte[] colorTable,
            GraphicControl control,
            int left,
            int destinationRow,
            int width)
        {
            int destination = (destinationRow * _width + left) * 4;
            for (int column = 0; column < width; column++)
            {
                int colorIndex = indices[sourceOffset++];
                if (control.HasTransparency &&
                    colorIndex == control.TransparentColorIndex)
                {
                    destination += 4;
                    continue;
                }

                int paletteOffset = colorIndex * 3;
                if (paletteOffset > colorTable.Length - 3)
                {
                    throw new InvalidDataException(
                        "GIF pixel references a color outside the active table.");
                }

                _canvas[destination++] = colorTable[paletteOffset];
                _canvas[destination++] = colorTable[paletteOffset + 1];
                _canvas[destination++] = colorTable[paletteOffset + 2];
                _canvas[destination++] = 255;
            }
        }

        private Animation Finish()
        {
            if (_hasPendingControl)
            {
                throw new InvalidDataException(
                    "GIF ends with an unused graphic-control extension.");
            }
            if (_frames.Count == 0)
            {
                throw new InvalidDataException("GIF does not contain an image frame.");
            }
            if (!_reader.IsAtEnd)
            {
                throw new InvalidDataException(
                    "GIF contains trailing data after the trailer.");
            }

            return new Animation(
                _width,
                _height,
                _loopCount == 0,
                _frames);
        }
    }

    private readonly struct GraphicControl
    {
        public static readonly GraphicControl Default = new(
            DisposalMethod.NotSpecified,
            requiresUserInput: false,
            hasTransparency: false,
            transparentColorIndex: 0,
            delayCentiseconds: 0);

        public readonly DisposalMethod Disposal;
        public readonly bool RequiresUserInput;
        public readonly bool HasTransparency;
        public readonly int TransparentColorIndex;
        public readonly int DelayCentiseconds;

        public GraphicControl(
            DisposalMethod disposal,
            bool requiresUserInput,
            bool hasTransparency,
            int transparentColorIndex,
            int delayCentiseconds)
        {
            Disposal = disposal;
            RequiresUserInput = requiresUserInput;
            HasTransparency = hasTransparency;
            TransparentColorIndex = transparentColorIndex;
            DelayCentiseconds = delayCentiseconds;
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;

        public Reader(byte[] data)
        {
            _data = data;
        }

        public int Position { get; private set; }

        public bool IsAtEnd => Position == _data.Length;

        public byte ReadByte(string context)
        {
            Require(1, context);
            return _data[Position++];
        }

        public int ReadUInt16(string context)
        {
            Require(2, context);
            int value = _data[Position] | (_data[Position + 1] << 8);
            Position += 2;
            return value;
        }

        public string ReadAscii(int count, string context)
        {
            Require(count, context);
            string value = Encoding.ASCII.GetString(_data, Position, count);
            Position += count;
            return value;
        }

        public byte[] ReadColorTable(int entryCount, string context)
        {
            int byteCount = entryCount * 3;
            Require(byteCount, context);
            byte[] table = new byte[byteCount];
            Buffer.BlockCopy(_data, Position, table, 0, byteCount);
            Position += byteCount;
            return table;
        }

        public void SkipSubBlocks(string context)
        {
            while (true)
            {
                int blockSize = ReadByte($"{context} block size");
                if (blockSize == 0)
                {
                    return;
                }

                Require(blockSize, context);
                Position += blockSize;
            }
        }

        private void Require(int count, string context)
        {
            if (count < 0 || Position > _data.Length - count)
            {
                throw new InvalidDataException(
                    $"GIF ended while reading {context}.");
            }
        }
    }

    private sealed class SubBlockBitReader
    {
        private readonly Reader _reader;
        private int _remainingInBlock;
        private uint _bitBuffer;
        private int _bitCount;
        private bool _finished;

        public SubBlockBitReader(Reader reader)
        {
            _reader = reader;
        }

        public int ReadCode(int codeSize)
        {
            if (_finished)
            {
                throw new InvalidDataException(
                    "GIF LZW stream was read after its terminator.");
            }

            while (_bitCount < codeSize)
            {
                int value = ReadDataByte();
                if (value < 0)
                {
                    throw new InvalidDataException(
                        "GIF LZW stream ended before an end code.");
                }

                _bitBuffer |= (uint)value << _bitCount;
                _bitCount += 8;
            }

            int mask = (1 << codeSize) - 1;
            int code = (int)(_bitBuffer & (uint)mask);
            _bitBuffer >>= codeSize;
            _bitCount -= codeSize;
            return code;
        }

        public void FinishAfterEndCode()
        {
            if (_finished)
            {
                throw new InvalidDataException(
                    "GIF LZW stream has multiple terminators.");
            }
            if (_remainingInBlock != 0)
            {
                throw new InvalidDataException(
                    "GIF LZW stream has bytes after its end code.");
            }
            if (_reader.ReadByte("LZW sub-block terminator") != 0)
            {
                throw new InvalidDataException(
                    "GIF LZW stream has data after its end code.");
            }

            _finished = true;
        }

        private int ReadDataByte()
        {
            if (_remainingInBlock == 0)
            {
                int blockSize = _reader.ReadByte("LZW sub-block size");
                if (blockSize == 0)
                {
                    return -1;
                }
                _remainingInBlock = blockSize;
            }

            _remainingInBlock--;
            return _reader.ReadByte("LZW image data");
        }
    }

    private static byte[] DecodeLzw(
        Reader reader,
        int minimumCodeSize,
        int expectedPixelCount)
    {
        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;
        int nextCode = endCode + 1;
        int codeSize = minimumCodeSize + 1;
        int oldCode = -1;
        byte firstCharacter = 0;
        bool sawClearCode = false;

        int[] prefixes = new int[MaximumLzwCodeCount];
        byte[] suffixes = new byte[MaximumLzwCodeCount];
        byte[] stack = new byte[MaximumLzwCodeCount + 1];
        byte[] output = new byte[expectedPixelCount];
        int outputOffset = 0;

        for (int index = 0; index < clearCode; index++)
        {
            suffixes[index] = (byte)index;
        }

        SubBlockBitReader bits = new(reader);
        while (true)
        {
            int code = bits.ReadCode(codeSize);
            if (code == clearCode)
            {
                codeSize = minimumCodeSize + 1;
                nextCode = endCode + 1;
                oldCode = -1;
                sawClearCode = true;
                continue;
            }
            if (!sawClearCode)
            {
                throw new InvalidDataException(
                    "GIF LZW stream does not begin with a clear code.");
            }
            if (code == endCode)
            {
                if (outputOffset != expectedPixelCount)
                {
                    throw new InvalidDataException(
                        "GIF LZW stream produced an unexpected pixel count.");
                }

                bits.FinishAfterEndCode();
                return output;
            }

            if (oldCode < 0)
            {
                if (code < 0 || code >= clearCode)
                {
                    throw new InvalidDataException(
                        "GIF LZW stream has an invalid first code after clear.");
                }

                WriteOutput(output, ref outputOffset, (byte)code);
                firstCharacter = (byte)code;
                oldCode = code;
                continue;
            }

            int inputCode = code;
            int stackOffset = 0;
            if (code == nextCode)
            {
                if (nextCode >= MaximumLzwCodeCount)
                {
                    throw new InvalidDataException(
                        "GIF LZW stream references an unavailable code.");
                }
                stack[stackOffset++] = firstCharacter;
                code = oldCode;
            }
            else if (code > nextCode)
            {
                throw new InvalidDataException(
                    "GIF LZW stream references an undefined code.");
            }

            int chainLength = 0;
            while (code >= clearCode)
            {
                if (code < endCode + 1 || code >= nextCode)
                {
                    throw new InvalidDataException(
                        "GIF LZW dictionary chain is invalid.");
                }
                if (stackOffset >= stack.Length ||
                    ++chainLength > MaximumLzwCodeCount)
                {
                    throw new InvalidDataException(
                        "GIF LZW dictionary chain is cyclic or too deep.");
                }

                stack[stackOffset++] = suffixes[code];
                code = prefixes[code];
            }

            if (code < 0 || code >= clearCode)
            {
                throw new InvalidDataException(
                    "GIF LZW dictionary does not end in a literal.");
            }

            firstCharacter = suffixes[code];
            if (stackOffset >= stack.Length)
            {
                throw new InvalidDataException("GIF LZW output stack overflowed.");
            }
            stack[stackOffset++] = firstCharacter;

            while (stackOffset > 0)
            {
                WriteOutput(output, ref outputOffset, stack[--stackOffset]);
            }

            if (nextCode < MaximumLzwCodeCount)
            {
                prefixes[nextCode] = oldCode;
                suffixes[nextCode] = firstCharacter;
                nextCode++;

                if (nextCode == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            oldCode = inputCode;
        }
    }

    private static void WriteOutput(
        byte[] output,
        ref int outputOffset,
        byte value)
    {
        if (outputOffset >= output.Length)
        {
            throw new InvalidDataException(
                "GIF LZW stream produced too many pixels.");
        }
        output[outputOffset++] = value;
    }
}
