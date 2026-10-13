using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace TermLink.TerminalCore
{
    [Flags]
    public enum NativeTerminalModifiers : uint
    {
        None = 0,
        Shift = 1,
        Alt = 2,
        Control = 4,
        Meta = 8
    }

    /// <summary>
    /// One immutable Win32 KEY_EVENT_RECORD keyboard event. UnicodeCharacter
    /// is one UTF-16 code unit and is not interpreted as a Unicode scalar.
    /// </summary>
    public readonly struct NativeTerminalWin32KeyEvent
    {
        public NativeTerminalWin32KeyEvent(
            ushort virtualKey,
            ushort scanCode,
            char unicodeCharacter,
            bool keyDown,
            uint controlKeyState,
            ushort repeatCount)
        {
            VirtualKey = virtualKey;
            ScanCode = scanCode;
            UnicodeCharacter = unicodeCharacter;
            KeyDown = keyDown;
            ControlKeyState = controlKeyState;
            RepeatCount = repeatCount;
        }

        public ushort VirtualKey { get; }
        public ushort ScanCode { get; }
        public char UnicodeCharacter { get; }
        public bool KeyDown { get; }
        public uint ControlKeyState { get; }
        public ushort RepeatCount { get; }
    }

    public enum NativeTerminalKey : uint
    {
        Character = 1,
        Enter = 2,
        Backspace = 3,
        Tab = 4,
        Escape = 5,
        Delete = 6,
        Insert = 7,
        Home = 8,
        End = 9,
        PageUp = 10,
        PageDown = 11,
        ArrowUp = 12,
        ArrowDown = 13,
        ArrowLeft = 14,
        ArrowRight = 15,
        F1 = 16,
        F2 = 17,
        F3 = 18,
        F4 = 19,
        F5 = 20,
        F6 = 21,
        F7 = 22,
        F8 = 23,
        F9 = 24,
        F10 = 25,
        F11 = 26,
        F12 = 27,
        Keypad0 = 100,
        Keypad1 = 101,
        Keypad2 = 102,
        Keypad3 = 103,
        Keypad4 = 104,
        Keypad5 = 105,
        Keypad6 = 106,
        Keypad7 = 107,
        Keypad8 = 108,
        Keypad9 = 109,
        KeypadDecimal = 110,
        KeypadSeparator = 111,
        KeypadAdd = 112,
        KeypadSubtract = 113,
        KeypadMultiply = 114,
        KeypadDivide = 115,
        KeypadEqual = 116,
        KeypadEnter = 117
    }

    public enum NativeTerminalKittyKeyEventType : uint
    {
        Press = 1,
        Repeat = 2,
        Release = 3
    }

    [Flags]
    public enum NativeTerminalKittyModifiers : uint
    {
        None = 0,
        Shift = 1,
        Alt = 2,
        Control = 4,
        Super = 8,
        Hyper = 16,
        Meta = 32,
        CapsLock = 64,
        NumLock = 128
    }

    public enum NativeTerminalKittyKeyIdentityKind
    {
        Character = 1,
        Functional = 2,
        UnidentifiedText = 3
    }

    /// <summary>
    /// The identity of one Kitty keyboard event. Functional identities use
    /// either an existing <see cref="NativeTerminalKey"/> value or one of the
    /// Kitty protocol private-use values 57358 through 57454.
    /// </summary>
    public readonly struct NativeTerminalKittyKeyIdentity
    {
        public const uint FirstPrivateUseFunctionalKey = 57_358;
        public const uint LastPrivateUseFunctionalKey = 57_454;

        private readonly Rune character;
        private readonly uint functionalKey;

        private NativeTerminalKittyKeyIdentity(
            NativeTerminalKittyKeyIdentityKind kind,
            Rune character,
            uint functionalKey)
        {
            Kind = kind;
            this.character = character;
            this.functionalKey = functionalKey;
        }

        public NativeTerminalKittyKeyIdentityKind Kind { get; }

        public Rune CharacterValue
        {
            get
            {
                if (Kind != NativeTerminalKittyKeyIdentityKind.Character)
                    throw new InvalidOperationException(
                        "This Kitty key identity is not a character.");
                return character;
            }
        }

        public uint FunctionalKeyValue
        {
            get
            {
                if (Kind != NativeTerminalKittyKeyIdentityKind.Functional)
                    throw new InvalidOperationException(
                        "This Kitty key identity is not a functional key.");
                return functionalKey;
            }
        }

        public static NativeTerminalKittyKeyIdentity Character(Rune character)
        {
            ValidateTextRune(character, nameof(character));
            return new NativeTerminalKittyKeyIdentity(
                NativeTerminalKittyKeyIdentityKind.Character,
                character,
                0);
        }

        public static NativeTerminalKittyKeyIdentity Functional(NativeTerminalKey key)
        {
            if (!IsCommonFunctionalKey((uint)key))
                throw new ArgumentOutOfRangeException(
                    nameof(key),
                    "The key is not a Kitty-compatible common functional key.");
            return new NativeTerminalKittyKeyIdentity(
                NativeTerminalKittyKeyIdentityKind.Functional,
                default,
                (uint)key);
        }

        public static NativeTerminalKittyKeyIdentity Functional(uint privateUseKey)
        {
            if (privateUseKey < FirstPrivateUseFunctionalKey ||
                privateUseKey > LastPrivateUseFunctionalKey)
                throw new ArgumentOutOfRangeException(
                    nameof(privateUseKey),
                    "A Kitty private-use functional key must be in 57358 through 57454.");
            return new NativeTerminalKittyKeyIdentity(
                NativeTerminalKittyKeyIdentityKind.Functional,
                default,
                privateUseKey);
        }

        public static NativeTerminalKittyKeyIdentity UnidentifiedText { get; } =
            new NativeTerminalKittyKeyIdentity(
                NativeTerminalKittyKeyIdentityKind.UnidentifiedText,
                default,
                0);

        internal uint KeyValue
        {
            get
            {
                switch (Kind)
                {
                    case NativeTerminalKittyKeyIdentityKind.Character:
                        return (uint)NativeTerminalKey.Character;
                    case NativeTerminalKittyKeyIdentityKind.Functional:
                        return functionalKey;
                    case NativeTerminalKittyKeyIdentityKind.UnidentifiedText:
                        return uint.MaxValue;
                    default:
                        throw new ArgumentException("Unknown Kitty key identity.", nameof(Kind));
                }
            }
        }

        internal uint CharacterScalar =>
            Kind == NativeTerminalKittyKeyIdentityKind.Character
                ? checked((uint)character.Value)
                : 0;

        internal void Validate(string parameterName)
        {
            switch (Kind)
            {
                case NativeTerminalKittyKeyIdentityKind.Character:
                    ValidateTextRune(character, parameterName);
                    break;
                case NativeTerminalKittyKeyIdentityKind.Functional:
                    if (!IsCommonFunctionalKey(functionalKey) &&
                        (functionalKey < FirstPrivateUseFunctionalKey ||
                            functionalKey > LastPrivateUseFunctionalKey))
                        throw new ArgumentException(
                            "Unknown Kitty functional key identity.",
                            parameterName);
                    break;
                case NativeTerminalKittyKeyIdentityKind.UnidentifiedText:
                    break;
                default:
                    throw new ArgumentException("Unknown Kitty key identity.", parameterName);
            }
        }

        internal static void ValidateTextRune(Rune value, string parameterName)
        {
            if (value.Value <= 0x1f || (value.Value >= 0x7f && value.Value <= 0x9f))
                throw new ArgumentException(
                    "Kitty key text cannot contain C0 or C1 control characters.",
                    parameterName);
        }

        private static bool IsCommonFunctionalKey(uint key)
        {
            return (key >= (uint)NativeTerminalKey.Enter &&
                    key <= (uint)NativeTerminalKey.F12) ||
                (key >= (uint)NativeTerminalKey.Keypad0 &&
                    key <= (uint)NativeTerminalKey.KeypadEnter);
        }
    }

    /// <summary>
    /// One semantic Kitty keyboard event. Native encoding remains authoritative
    /// for negotiated protocol modes and associated-text size limits.
    /// </summary>
    public readonly struct NativeTerminalKittyKeyEvent
    {
        private const uint AllModifiers = (uint)(
            NativeTerminalKittyModifiers.Shift |
            NativeTerminalKittyModifiers.Alt |
            NativeTerminalKittyModifiers.Control |
            NativeTerminalKittyModifiers.Super |
            NativeTerminalKittyModifiers.Hyper |
            NativeTerminalKittyModifiers.Meta |
            NativeTerminalKittyModifiers.CapsLock |
            NativeTerminalKittyModifiers.NumLock);

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public NativeTerminalKittyKeyEvent(
            NativeTerminalKittyKeyEventType eventType,
            NativeTerminalKittyModifiers modifiers,
            NativeTerminalKittyKeyIdentity identity,
            Rune? shiftedKey = null,
            Rune? baseLayoutKey = null,
            string associatedText = null)
        {
            EventType = eventType;
            Modifiers = modifiers;
            Identity = identity;
            ShiftedKey = shiftedKey;
            BaseLayoutKey = baseLayoutKey;
            AssociatedText = associatedText ?? string.Empty;
            Validate(nameof(identity));
        }

        public NativeTerminalKittyKeyEventType EventType { get; }
        public NativeTerminalKittyModifiers Modifiers { get; }
        public NativeTerminalKittyKeyIdentity Identity { get; }
        public Rune? ShiftedKey { get; }
        public Rune? BaseLayoutKey { get; }
        public string AssociatedText { get; }

        internal void Validate(string parameterName)
        {
            if (EventType != NativeTerminalKittyKeyEventType.Press &&
                EventType != NativeTerminalKittyKeyEventType.Repeat &&
                EventType != NativeTerminalKittyKeyEventType.Release)
                throw new ArgumentException("Unknown Kitty key event type.", parameterName);
            if (((uint)Modifiers & ~AllModifiers) != 0)
                throw new ArgumentException(
                    "Kitty keyboard modifier flags contain unknown bits.",
                    parameterName);
            Identity.Validate(parameterName);
            if (ShiftedKey.HasValue)
            {
                NativeTerminalKittyKeyIdentity.ValidateTextRune(
                    ShiftedKey.Value,
                    parameterName);
                if ((Modifiers & NativeTerminalKittyModifiers.Shift) == 0)
                    throw new ArgumentException(
                        "A shifted alternate requires the Shift modifier.",
                        parameterName);
            }
            if (BaseLayoutKey.HasValue)
                NativeTerminalKittyKeyIdentity.ValidateTextRune(
                    BaseLayoutKey.Value,
                    parameterName);
            if (Identity.Kind != NativeTerminalKittyKeyIdentityKind.Character &&
                (ShiftedKey.HasValue || BaseLayoutKey.HasValue))
                throw new ArgumentException(
                    "Alternate key values are valid only for character identities.",
                    parameterName);

            var associatedText = AssociatedText ?? string.Empty;
            try
            {
                _ = StrictUtf8.GetByteCount(associatedText);
            }
            catch (EncoderFallbackException error)
            {
                throw new ArgumentException(
                    "Associated text must contain valid Unicode scalar values.",
                    parameterName,
                    error);
            }
            foreach (var character in associatedText.EnumerateRunes())
                NativeTerminalKittyKeyIdentity.ValidateTextRune(character, parameterName);

            if (EventType == NativeTerminalKittyKeyEventType.Release &&
                associatedText.Length != 0)
                throw new ArgumentException(
                    "A Kitty release event cannot carry associated text.",
                    parameterName);
            if (Identity.Kind == NativeTerminalKittyKeyIdentityKind.UnidentifiedText)
            {
                if (EventType != NativeTerminalKittyKeyEventType.Press)
                    throw new ArgumentException(
                        "An unidentified-text key must be a press event.",
                        parameterName);
                if (associatedText.Length == 0)
                    throw new ArgumentException(
                        "An unidentified-text key requires associated text.",
                        parameterName);
            }
        }

        internal byte[] EncodeAssociatedText()
        {
            return StrictUtf8.GetBytes(AssociatedText ?? string.Empty);
        }
    }

    public enum NativeTerminalMouseAction : uint
    {
        Press = 1,
        Release = 2,
        Move = 3,
        Wheel = 4
    }

    public enum NativeTerminalMouseButton : uint
    {
        Left = 1,
        Middle = 2,
        Right = 3
    }

    public enum NativeTerminalMouseWheelDirection : uint
    {
        Up = 1,
        Down = 2,
        Left = 3,
        Right = 4
    }

    [Flags]
    public enum NativeTerminalMouseButtons : uint
    {
        None = 0,
        Left = 1,
        Middle = 2,
        Right = 4
    }

    /// <summary>
    /// A mouse location whose cell and optional pixel coordinates are zero-based.
    /// Pixel coordinates are required by mode 1016 and are optional in other modes.
    /// </summary>
    public readonly struct NativeTerminalMousePosition
    {
        private NativeTerminalMousePosition(
            uint cellColumn,
            uint cellRow,
            bool hasPixelPosition,
            uint pixelX,
            uint pixelY)
        {
            CellColumn = cellColumn;
            CellRow = cellRow;
            HasPixelPosition = hasPixelPosition;
            PixelX = pixelX;
            PixelY = pixelY;
        }

        public uint CellColumn { get; }
        public uint CellRow { get; }
        public bool HasPixelPosition { get; }
        public uint PixelX { get; }
        public uint PixelY { get; }

        public static NativeTerminalMousePosition Cell(
            uint zeroBasedColumn,
            uint zeroBasedRow)
        {
            return new NativeTerminalMousePosition(
                zeroBasedColumn,
                zeroBasedRow,
                false,
                0,
                0);
        }

        public static NativeTerminalMousePosition CellAndPixel(
            uint zeroBasedCellColumn,
            uint zeroBasedCellRow,
            uint zeroBasedPixelX,
            uint zeroBasedPixelY)
        {
            return new NativeTerminalMousePosition(
                zeroBasedCellColumn,
                zeroBasedCellRow,
                true,
                zeroBasedPixelX,
                zeroBasedPixelY);
        }

        internal void Validate(string parameterName)
        {
            if (!HasPixelPosition && (PixelX != 0 || PixelY != 0))
                throw new ArgumentException(
                    "Pixel coordinates require an explicit pixel position.",
                    parameterName);
        }
    }

    /// <summary>
    /// An immutable semantic mouse event. Use the action-specific factories to
    /// preserve the action payload and post-event held-button invariants.
    /// </summary>
    public readonly struct NativeTerminalMouseEvent
    {
        private const uint AllButtons = (uint)(
            NativeTerminalMouseButtons.Left |
            NativeTerminalMouseButtons.Middle |
            NativeTerminalMouseButtons.Right);
        private const uint AllModifiers = (uint)(
            NativeTerminalModifiers.Shift |
            NativeTerminalModifiers.Alt |
            NativeTerminalModifiers.Control |
            NativeTerminalModifiers.Meta);

        private readonly NativeTerminalMouseButton button;
        private readonly NativeTerminalMouseWheelDirection wheelDirection;

        private NativeTerminalMouseEvent(
            NativeTerminalMouseAction action,
            NativeTerminalMouseButton button,
            NativeTerminalMouseWheelDirection wheelDirection,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers)
        {
            Action = action;
            this.button = button;
            this.wheelDirection = wheelDirection;
            ButtonsHeld = buttonsHeld;
            Position = position;
            Modifiers = modifiers;
        }

        public NativeTerminalMouseAction Action { get; }
        public NativeTerminalMouseButton? Button =>
            Action == NativeTerminalMouseAction.Press ||
            Action == NativeTerminalMouseAction.Release
                ? button
                : (NativeTerminalMouseButton?)null;
        public NativeTerminalMouseWheelDirection? WheelDirection =>
            Action == NativeTerminalMouseAction.Wheel
                ? wheelDirection
                : (NativeTerminalMouseWheelDirection?)null;
        public NativeTerminalMouseButtons ButtonsHeld { get; }
        public NativeTerminalMousePosition Position { get; }
        public NativeTerminalModifiers Modifiers { get; }

        public static NativeTerminalMouseEvent Press(
            NativeTerminalMouseButton button,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            ValidateButton(button, nameof(button));
            ValidateCommon(buttonsHeld, position, modifiers);
            if (!Contains(buttonsHeld, button))
                throw new ArgumentException(
                    "A press event's post-event state must include its button.",
                    nameof(buttonsHeld));
            return new NativeTerminalMouseEvent(
                NativeTerminalMouseAction.Press,
                button,
                default,
                buttonsHeld,
                position,
                modifiers);
        }

        public static NativeTerminalMouseEvent Release(
            NativeTerminalMouseButton button,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            ValidateButton(button, nameof(button));
            ValidateCommon(buttonsHeld, position, modifiers);
            if (Contains(buttonsHeld, button))
                throw new ArgumentException(
                    "A release event's post-event state must exclude its button.",
                    nameof(buttonsHeld));
            return new NativeTerminalMouseEvent(
                NativeTerminalMouseAction.Release,
                button,
                default,
                buttonsHeld,
                position,
                modifiers);
        }

        public static NativeTerminalMouseEvent Move(
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            ValidateCommon(buttonsHeld, position, modifiers);
            return new NativeTerminalMouseEvent(
                NativeTerminalMouseAction.Move,
                default,
                default,
                buttonsHeld,
                position,
                modifiers);
        }

        public static NativeTerminalMouseEvent Wheel(
            NativeTerminalMouseWheelDirection direction,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            ValidateWheelDirection(direction, nameof(direction));
            ValidateCommon(buttonsHeld, position, modifiers);
            return new NativeTerminalMouseEvent(
                NativeTerminalMouseAction.Wheel,
                default,
                direction,
                buttonsHeld,
                position,
                modifiers);
        }

        internal void Validate(string parameterName)
        {
            ValidateCommon(ButtonsHeld, Position, Modifiers, parameterName);
            switch (Action)
            {
                case NativeTerminalMouseAction.Press:
                    ValidateButton(button, parameterName);
                    if (!Contains(ButtonsHeld, button))
                        throw new ArgumentException(
                            "A press event's post-event state must include its button.",
                            parameterName);
                    break;
                case NativeTerminalMouseAction.Release:
                    ValidateButton(button, parameterName);
                    if (Contains(ButtonsHeld, button))
                        throw new ArgumentException(
                            "A release event's post-event state must exclude its button.",
                            parameterName);
                    break;
                case NativeTerminalMouseAction.Move:
                    break;
                case NativeTerminalMouseAction.Wheel:
                    ValidateWheelDirection(wheelDirection, parameterName);
                    break;
                default:
                    throw new ArgumentException("Unknown mouse action.", parameterName);
            }
        }

        internal uint ButtonValue =>
            Action == NativeTerminalMouseAction.Press ||
            Action == NativeTerminalMouseAction.Release
                ? (uint)button
                : 0;

        internal uint WheelValue =>
            Action == NativeTerminalMouseAction.Wheel
                ? (uint)wheelDirection
                : 0;

        private static void ValidateCommon(
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers,
            string parameterName = null)
        {
            if (((uint)buttonsHeld & ~AllButtons) != 0)
                throw new ArgumentOutOfRangeException(
                    parameterName ?? nameof(buttonsHeld),
                    "Mouse button flags contain unknown bits.");
            if (((uint)modifiers & ~AllModifiers) != 0)
                throw new ArgumentOutOfRangeException(
                    parameterName ?? nameof(modifiers),
                    "Mouse modifier flags contain unknown bits.");
            position.Validate(parameterName ?? nameof(position));
        }

        private static void ValidateButton(
            NativeTerminalMouseButton value,
            string parameterName)
        {
            if (value != NativeTerminalMouseButton.Left &&
                value != NativeTerminalMouseButton.Middle &&
                value != NativeTerminalMouseButton.Right)
                throw new ArgumentOutOfRangeException(parameterName, "Unknown mouse button.");
        }

        private static void ValidateWheelDirection(
            NativeTerminalMouseWheelDirection value,
            string parameterName)
        {
            if (value != NativeTerminalMouseWheelDirection.Up &&
                value != NativeTerminalMouseWheelDirection.Down &&
                value != NativeTerminalMouseWheelDirection.Left &&
                value != NativeTerminalMouseWheelDirection.Right)
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Unknown mouse wheel direction.");
        }

        private static bool Contains(
            NativeTerminalMouseButtons buttons,
            NativeTerminalMouseButton button)
        {
            uint flag;
            switch (button)
            {
                case NativeTerminalMouseButton.Left:
                    flag = (uint)NativeTerminalMouseButtons.Left;
                    break;
                case NativeTerminalMouseButton.Middle:
                    flag = (uint)NativeTerminalMouseButtons.Middle;
                    break;
                case NativeTerminalMouseButton.Right:
                    flag = (uint)NativeTerminalMouseButtons.Right;
                    break;
                default:
                    return false;
            }
            return ((uint)buttons & flag) != 0;
        }
    }

    public enum NativeTerminalBatchEventKind : uint
    {
        Output = 1,
        Resize = 2,
        Advance = 3,
        Gap = 4,
        HostGeometryV1 = 5,
        HostThemeV1 = 6
    }

    public sealed class NativeTerminalBatchEvent
    {
        public const int MaxOutputBytes = 1024 * 1024;

        private readonly byte[] outputBytes;

        private NativeTerminalBatchEvent(
            NativeTerminalBatchEventKind kind,
            ulong sourceSequence,
            byte[] outputBytes,
            uint columns,
            uint rows,
            ulong advanceMicros)
        {
            Kind = kind;
            SourceSequence = sourceSequence;
            this.outputBytes = outputBytes;
            Columns = columns;
            Rows = rows;
            AdvanceMicros = advanceMicros;
        }

        public NativeTerminalBatchEventKind Kind { get; }
        public ulong SourceSequence { get; }
        internal byte[] OutputBytes => outputBytes;
        internal uint Columns { get; }
        internal uint Rows { get; }
        internal ulong AdvanceMicros { get; }

        public static NativeTerminalBatchEvent Output(ulong sourceSequence, byte[] output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (output.Length == 0 || output.Length > MaxOutputBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(output),
                    "Batch output must contain 1 through 1,048,576 bytes.");
            return new NativeTerminalBatchEvent(
                NativeTerminalBatchEventKind.Output,
                sourceSequence,
                (byte[])output.Clone(),
                0,
                0,
                0);
        }

        public static NativeTerminalBatchEvent HostTheme(ulong sourceSequence, NativeTerminalHostThemeV1 theme)
        {
            return new NativeTerminalBatchEvent(NativeTerminalBatchEventKind.HostThemeV1,
                sourceSequence, theme.Encode(), 0, 0, 0);
        }

        public static NativeTerminalBatchEvent HostGeometry(ulong sourceSequence, NativeTerminalHostGeometryV1 geometry)
        {
            return new NativeTerminalBatchEvent(NativeTerminalBatchEventKind.HostGeometryV1,
                sourceSequence, geometry.Encode(), 0, 0, 0);
        }

        public static NativeTerminalBatchEvent Resize(
            ulong sourceSequence,
            uint columns,
            uint rows)
        {
            return new NativeTerminalBatchEvent(
                NativeTerminalBatchEventKind.Resize,
                sourceSequence,
                null,
                columns,
                rows,
                0);
        }

        public static NativeTerminalBatchEvent Advance(
            ulong sourceSequence,
            ulong micros)
        {
            return new NativeTerminalBatchEvent(
                NativeTerminalBatchEventKind.Advance,
                sourceSequence,
                null,
                0,
                0,
                micros);
        }

        public static NativeTerminalBatchEvent Gap(ulong sourceSequence)
        {
            return new NativeTerminalBatchEvent(
                NativeTerminalBatchEventKind.Gap,
                sourceSequence,
                null,
                0,
                0,
                0);
        }
    }

    /// <summary>
    /// Selects how terminal output bytes in the C1 range are interpreted.
    /// </summary>
    public enum NativeTerminalOutputProfile : uint
    {
        Utf8 = 0,
        Utf8WithStandaloneC1 = 1
    }

    public enum NativeTerminalStatus : uint
    {
        Ok = 0,
        NullPointer = 1,
        InvalidArgument = 2,
        InvalidDimensions = 3,
        LimitExceeded = 4,
        SequenceMismatch = 5,
        FailedClosed = 6,
        Unsupported = 7,
        Gap = 8,
        MalformedSnapshot = 9,
        IncompatibleSnapshot = 10,
        TimeOverflow = 11,
        SnapshotNotEligible = 12,
        PresentationDeferred = 13,
        HostEffectVersionRequired = 14,
        RenderFrameVersionRequired = 15,
        HistoryStaleCapture = 16,
        HistoryStaleRevision = 17,
        HistoryStaleAnchor = 18,
        HistoryPageBudget = 19,
        MalformedDelta = 20,
        IncompatibleDelta = 21,
        NeedsFullSnapshot = 22,
        Panic = 255
    }

    [Flags]
    public enum NativeTerminalPresentationFlags : uint
    {
        None = 0,
        FailedClosed = 1,
        ParserIncomplete = 2,
        SynchronizedOutput = 4
    }

    public sealed class NativeTerminalException : Exception
    {
        internal NativeTerminalException(string operation, NativeTerminalStatus status, string diagnostic = null)
            : base(operation + " failed with native terminal status " +
                status + " (" + ((uint)status).ToString() + ").")
        {
            Operation = operation;
            Status = status;
            Diagnostic = diagnostic;
        }

        public string Operation { get; }
        public NativeTerminalStatus Status { get; }
        // Local escaped evidence is deliberately excluded from generic exception telemetry.
        public string Diagnostic { get; }
    }

    public sealed class NativeTerminalAbiException : Exception
    {
        internal NativeTerminalAbiException(string message) : base(message)
        {
        }

        internal NativeTerminalAbiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public readonly struct NativeTerminalAbiInfo
    {
        internal NativeTerminalAbiInfo(
            uint abiVersion,
            ulong maxSnapshotBytes,
            uint renderFrameV1FormatVersion,
            ulong maxRenderFrameV1Bytes,
            uint renderFrameV2FormatVersion,
            ulong maxRenderFrameV2Bytes,
            uint renderFrameV3FormatVersion,
            ulong maxRenderFrameV3Bytes,
            uint hostEffectFrameV1FormatVersion,
            ulong maxHostEffectFrameV1Bytes,
            uint hostEffectFrameV2FormatVersion,
            ulong maxHostEffectFrameV2Bytes,
            uint configV1StructSize,
            uint configV2StructSize,
            uint configV3StructSize,
            uint eventStructSize,
            uint bufferStructSize,
            uint checkpointInfoStructSize,
            uint presentationInfoV1StructSize,
            uint inputV1StructSize,
            uint inputV2StructSize,
            uint inputV3StructSize,
            uint mouseInputV1StructSize)
        {
            AbiVersion = abiVersion;
            MaxSnapshotBytes = maxSnapshotBytes;
            RenderFrameV1FormatVersion = renderFrameV1FormatVersion;
            MaxRenderFrameV1Bytes = maxRenderFrameV1Bytes;
            RenderFrameV2FormatVersion = renderFrameV2FormatVersion;
            MaxRenderFrameV2Bytes = maxRenderFrameV2Bytes;
            RenderFrameV3FormatVersion = renderFrameV3FormatVersion;
            MaxRenderFrameV3Bytes = maxRenderFrameV3Bytes;
            HostEffectFrameV1FormatVersion = hostEffectFrameV1FormatVersion;
            MaxHostEffectFrameV1Bytes = maxHostEffectFrameV1Bytes;
            HostEffectFrameV2FormatVersion = hostEffectFrameV2FormatVersion;
            MaxHostEffectFrameV2Bytes = maxHostEffectFrameV2Bytes;
            ConfigV1StructSize = configV1StructSize;
            ConfigV2StructSize = configV2StructSize;
            ConfigV3StructSize = configV3StructSize;
            EventStructSize = eventStructSize;
            BufferStructSize = bufferStructSize;
            CheckpointInfoStructSize = checkpointInfoStructSize;
            PresentationInfoV1StructSize = presentationInfoV1StructSize;
            InputV1StructSize = inputV1StructSize;
            InputV2StructSize = inputV2StructSize;
            InputV3StructSize = inputV3StructSize;
            MouseInputV1StructSize = mouseInputV1StructSize;
        }

        public uint AbiVersion { get; }
        public ulong MaxSnapshotBytes { get; }
        public uint RenderFrameV1FormatVersion { get; }
        public ulong MaxRenderFrameV1Bytes { get; }
        public uint RenderFrameV2FormatVersion { get; }
        public ulong MaxRenderFrameV2Bytes { get; }
        public uint RenderFrameV3FormatVersion { get; }
        public ulong MaxRenderFrameV3Bytes { get; }
        public uint HostEffectFrameV1FormatVersion { get; }
        public ulong MaxHostEffectFrameV1Bytes { get; }
        public uint HostEffectFrameV2FormatVersion { get; }
        public ulong MaxHostEffectFrameV2Bytes { get; }
        public uint ConfigV1StructSize { get; }
        public uint ConfigV2StructSize { get; }
        public uint ConfigV3StructSize { get; }
        public uint EventStructSize { get; }
        public uint BufferStructSize { get; }
        public uint CheckpointInfoStructSize { get; }
        public uint PresentationInfoV1StructSize { get; }
        public uint InputV1StructSize { get; }
        public uint InputV2StructSize { get; }
        public uint InputV3StructSize { get; }
        public uint MouseInputV1StructSize { get; }
    }

    public readonly struct NativeTerminalCheckpointInfo
    {
        internal NativeTerminalCheckpointInfo(ulong revision, ulong nextSourceSequence)
        {
            Revision = revision;
            NextSourceSequence = nextSourceSequence;
        }

        public ulong Revision { get; }
        public ulong NextSourceSequence { get; }
    }

    public readonly struct NativeTerminalPresentationInfo
    {
        internal NativeTerminalPresentationInfo(
            NativeTerminalPresentationFlags flags,
            ulong nextSourceSequence,
            ulong logicalTimeMicros,
            ulong synchronizedOutputDeadlineMicros)
        {
            Flags = flags;
            NextSourceSequence = nextSourceSequence;
            LogicalTimeMicros = logicalTimeMicros;
            SynchronizedOutputDeadlineMicros = synchronizedOutputDeadlineMicros;
        }

        public NativeTerminalPresentationFlags Flags { get; }
        public ulong NextSourceSequence { get; }
        public ulong LogicalTimeMicros { get; }
        public ulong SynchronizedOutputDeadlineMicros { get; }
    }

    public static class NativeTerminalCoreV2
    {
        public const uint AbiVersion = 1;
        public const int MaximumSnapshotBytes = 64 * 1024 * 1024;
        public const int MaximumGraphicsSnapshotBytes = 128 * 1024 * 1024;

        private static readonly Lazy<NativeTerminalAbiInfo> AbiInfo = new Lazy<NativeTerminalAbiInfo>(
            NativeTerminalInterop.ValidateAbi,
            LazyThreadSafetyMode.ExecutionAndPublication);

        public static NativeTerminalAbiInfo ValidateAbi()
        {
            return AbiInfo.Value;
        }

        public static NativeTerminal Create(
            uint columns,
            uint rows,
            ulong initialSourceSequence = 1,
            ulong historyCapacityRows = 0)
        {
            _ = ValidateAbi();
            return NativeTerminal.Create(
                columns,
                rows,
                initialSourceSequence,
                historyCapacityRows);
        }

        /// <summary>
        /// Creates a terminal with the additive V3 output profile configuration.
        /// </summary>
        public static NativeTerminal Create(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows,
            NativeTerminalOutputProfile outputProfile)
        {
            _ = ValidateAbi();
            return NativeTerminal.Create(
                columns,
                rows,
                initialSourceSequence,
                historyCapacityRows,
                outputProfile);
        }

        /// <summary>Creates a V4 terminal without changing the legacy factory defaults.</summary>
        public static NativeTerminal Create(uint columns, uint rows, ulong initialSourceSequence,
            ulong historyCapacityRows, NativeTerminalOutputProfile outputProfile, NativeTerminalWidthProfile widthProfile)
        {
            _ = ValidateAbi();
            NativeTerminalInterop.RequireV4Abi();
            return NativeTerminal.Create(columns, rows, initialSourceSequence, historyCapacityRows, outputProfile, widthProfile);
        }

        /// <summary>Checks the optional graphics ABI; an older DLL reports false.</summary>
        public static bool SupportsGraphicsV1()
        {
            _ = ValidateAbi();
            return NativeTerminalInterop.SupportsGraphicsV1();
        }

        /// <summary>Explicitly enables the experimental graphics profile.</summary>
        public static NativeTerminal CreateGraphicsV1(uint columns, uint rows, ulong initialSourceSequence,
            ulong historyCapacityRows, NativeTerminalOutputProfile outputProfile, NativeTerminalWidthProfile widthProfile)
        {
            _ = ValidateAbi();
            NativeTerminalInterop.RequireGraphicsV1Abi();
            return NativeTerminal.CreateGraphicsV1(columns, rows, initialSourceSequence, historyCapacityRows, outputProfile, widthProfile);
        }

        public static NativeTerminal ImportSnapshot(byte[] snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return ImportSnapshot((ReadOnlySpan<byte>)snapshot);
        }

        public static NativeTerminal ImportSnapshot(ReadOnlySpan<byte> snapshot)
        {
            if (snapshot.Length > MaximumSnapshotBytes) throw new ArgumentOutOfRangeException(nameof(snapshot));
            if (IsGraphicsSnapshot(snapshot))
                throw new NativeTerminalException(nameof(ImportSnapshot), NativeTerminalStatus.IncompatibleSnapshot);
            _ = ValidateAbi();
            return NativeTerminal.ImportSnapshot(snapshot);
        }

        public static NativeTerminal ImportGraphicsSnapshot(byte[] snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return ImportGraphicsSnapshot((ReadOnlySpan<byte>)snapshot);
        }

        public static NativeTerminal ImportGraphicsSnapshot(ReadOnlySpan<byte> snapshot)
        {
            if (snapshot.Length > MaximumGraphicsSnapshotBytes) throw new ArgumentOutOfRangeException(nameof(snapshot));
            if (!IsGraphicsSnapshot(snapshot))
                throw new NativeTerminalException(nameof(ImportGraphicsSnapshot), NativeTerminalStatus.IncompatibleSnapshot);
            _ = ValidateAbi();
            NativeTerminalInterop.RequireGraphicsV1Abi();
            return NativeTerminal.ImportSnapshot(snapshot, true);
        }

        private static bool IsGraphicsSnapshot(ReadOnlySpan<byte> snapshot) => snapshot.Length >= 12 &&
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.Slice(8, 2)) == 2 &&
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.Slice(10, 2)) == 22;
    }

    public sealed partial class NativeTerminal : IDisposable
    {
        public const int MaxBatchEventCount = 256;
        public const int MaxBatchOutputBytes = 4 * 1024 * 1024;

        private readonly object sync = new object();
        private SafeTlTerminalHandle handle;
        private bool brokerActive;
        private bool disposed;
        private bool failedClosed;

        internal NativeTerminal(SafeTlTerminalHandle handle, bool hasGraphicsProfile = false)
        {
            this.handle = handle;
            HasGraphicsProfile = hasGraphicsProfile;
        }

        public bool HasGraphicsProfile { get; }

        internal static NativeTerminal Create(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows)
        {
            var nativeHandle = IntPtr.Zero;
            NativeTerminalStatus status;
            string operation;
            if (historyCapacityRows == 0)
            {
                var config = NativeTerminalInterop.CreateConfig(
                    columns,
                    rows,
                    initialSourceSequence);
                status = NativeTerminalInterop.TerminalCreate(in config, ref nativeHandle);
                operation = nameof(NativeTerminalInterop.TerminalCreate);
            }
            else
            {
                var config = NativeTerminalInterop.CreateConfigV2(
                    columns,
                    rows,
                    initialSourceSequence,
                    historyCapacityRows);
                status = NativeTerminalInterop.TerminalCreateV2(in config, ref nativeHandle);
                operation = nameof(NativeTerminalInterop.TerminalCreateV2);
            }
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                status,
                nativeHandle,
                operation);
            NativeTerminalInterop.ThrowIfFailed(status, operation);
            return new NativeTerminal(NativeTerminalInterop.OwnTerminal(nativeHandle));
        }

        internal static NativeTerminal Create(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows,
            NativeTerminalOutputProfile outputProfile)
        {
            var nativeHandle = IntPtr.Zero;
            var config = NativeTerminalInterop.CreateConfigV3(
                columns,
                rows,
                initialSourceSequence,
                historyCapacityRows,
                outputProfile);
            var status = NativeTerminalInterop.TerminalCreateV3(in config, ref nativeHandle);
            const string operation = nameof(NativeTerminalInterop.TerminalCreateV3);
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                status,
                nativeHandle,
                operation);
            NativeTerminalInterop.ThrowIfFailed(status, operation);
            return new NativeTerminal(NativeTerminalInterop.OwnTerminal(nativeHandle));
        }

        internal static NativeTerminal Create(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows,
            NativeTerminalOutputProfile outputProfile, NativeTerminalWidthProfile widthProfile)
        {
            var nativeHandle = IntPtr.Zero;
            var config = NativeTerminalInterop.CreateConfigV4(
                columns,
                rows,
                initialSourceSequence,
                historyCapacityRows,
                outputProfile, widthProfile);
            var status = NativeTerminalInterop.TerminalCreateV4(in config, ref nativeHandle);
            const string operation = nameof(NativeTerminalInterop.TerminalCreateV4);
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                status,
                nativeHandle,
                operation);
            NativeTerminalInterop.ThrowIfFailed(status, operation);
            return new NativeTerminal(NativeTerminalInterop.OwnTerminal(nativeHandle));
        }

        internal static NativeTerminal CreateGraphicsV1(uint columns, uint rows, ulong initialSourceSequence,
            ulong historyCapacityRows, NativeTerminalOutputProfile outputProfile, NativeTerminalWidthProfile widthProfile)
        {
            var nativeHandle = IntPtr.Zero;
            var config = NativeTerminalInterop.CreateConfigV4(columns, rows, initialSourceSequence,
                historyCapacityRows, outputProfile, widthProfile);
            var status = NativeTerminalInterop.TerminalCreateGraphicsV1(in config, ref nativeHandle);
            const string operation = nameof(NativeTerminalInterop.TerminalCreateGraphicsV1);
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(status, nativeHandle, operation);
            NativeTerminalInterop.ThrowIfFailed(status, operation);
            return new NativeTerminal(NativeTerminalInterop.OwnTerminal(nativeHandle), true);
        }

        internal static unsafe NativeTerminal ImportSnapshot(ReadOnlySpan<byte> snapshot, bool hasGraphicsProfile = false)
        {
            // The native import borrows only for this call. Own the pinned bytes so
            // the published terminal never depends on caller-managed storage.
            var ownedSnapshot = snapshot.ToArray();
            fixed (byte* snapshotPointer = ownedSnapshot)
            {
                var nativeHandle = IntPtr.Zero;
                var operation = hasGraphicsProfile ? nameof(NativeTerminalInterop.TerminalImportSnapshotGraphicsV1)
                    : nameof(NativeTerminalInterop.TerminalImportSnapshot);
                var status = hasGraphicsProfile
                    ? NativeTerminalInterop.TerminalImportSnapshotGraphicsV1(snapshotPointer, (ulong)ownedSnapshot.LongLength, ref nativeHandle)
                    : NativeTerminalInterop.TerminalImportSnapshot(snapshotPointer, (ulong)ownedSnapshot.LongLength, ref nativeHandle);
                NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                    status,
                    nativeHandle,
                    operation);
                NativeTerminalInterop.ThrowIfFailed(
                    status,
                    operation);
                return new NativeTerminal(NativeTerminalInterop.OwnTerminal(nativeHandle), hasGraphicsProfile);
            }
        }

        public void ApplyOutput(ulong sourceSequence, byte[] output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            ApplyOutput(sourceSequence, (ReadOnlySpan<byte>)output);
        }

        public unsafe void ApplyOutput(ulong sourceSequence, ReadOnlySpan<byte> output)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                fixed (byte* outputPointer = output)
                {
                    var terminalEvent = NativeTerminalInterop.CreateOutputEvent(
                        sourceSequence,
                        outputPointer,
                        checked((ulong)output.Length));
                    ApplyEvent(in terminalEvent);
                }
            }
        }

        public NativeTerminalHostEffectFrameV1 ApplyOutputWithHostEffectsV1(
            ulong sourceSequence,
            byte[] output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            return ApplyOutputWithHostEffectsV1(sourceSequence, (ReadOnlySpan<byte>)output);
        }

        public unsafe NativeTerminalHostEffectFrameV1 ApplyOutputWithHostEffectsV1(
            ulong sourceSequence,
            ReadOnlySpan<byte> output)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                fixed (byte* outputPointer = output)
                {
                    var terminalEvent = NativeTerminalInterop.CreateOutputEvent(
                        sourceSequence,
                        outputPointer,
                        checked((ulong)output.Length));
                    return ApplyEventWithHostEffectsV1(in terminalEvent);
                }
            }
        }

        public NativeTerminalHostEffectFrameV2 ApplyOutputWithHostEffectsV2(
            ulong sourceSequence,
            byte[] output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            return ApplyOutputWithHostEffectsV2(sourceSequence, (ReadOnlySpan<byte>)output);
        }

        public unsafe NativeTerminalHostEffectFrameV2 ApplyOutputWithHostEffectsV2(
            ulong sourceSequence,
            ReadOnlySpan<byte> output)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                fixed (byte* outputPointer = output)
                {
                    var terminalEvent = NativeTerminalInterop.CreateOutputEvent(
                        sourceSequence,
                        outputPointer,
                        checked((ulong)output.Length));
                    return ApplyEventWithHostEffectsV2(in terminalEvent);
                }
            }
        }

        public void ApplyResize(ulong sourceSequence, uint columns, uint rows)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateResizeEvent(
                    sourceSequence,
                    columns,
                    rows);
                ApplyEvent(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV1 ApplyResizeWithHostEffectsV1(
            ulong sourceSequence,
            uint columns,
            uint rows)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateResizeEvent(
                    sourceSequence,
                    columns,
                    rows);
                return ApplyEventWithHostEffectsV1(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV2 ApplyResizeWithHostEffectsV2(
            ulong sourceSequence,
            uint columns,
            uint rows)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateResizeEvent(
                    sourceSequence,
                    columns,
                    rows);
                return ApplyEventWithHostEffectsV2(in terminalEvent);
            }
        }

        public void ApplyAdvance(ulong sourceSequence, ulong micros)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateAdvanceEvent(sourceSequence, micros);
                ApplyEvent(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV1 ApplyAdvanceWithHostEffectsV1(
            ulong sourceSequence,
            ulong micros)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateAdvanceEvent(sourceSequence, micros);
                return ApplyEventWithHostEffectsV1(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV2 ApplyAdvanceWithHostEffectsV2(
            ulong sourceSequence,
            ulong micros)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateAdvanceEvent(sourceSequence, micros);
                return ApplyEventWithHostEffectsV2(in terminalEvent);
            }
        }

        public void ApplyGap(ulong sourceSequence)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateGapEvent(sourceSequence);
                ApplyEvent(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV1 ApplyGapWithHostEffectsV1(
            ulong sourceSequence)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateGapEvent(sourceSequence);
                return ApplyEventWithHostEffectsV1(in terminalEvent);
            }
        }

        public NativeTerminalHostEffectFrameV2 ApplyGapWithHostEffectsV2(
            ulong sourceSequence)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var terminalEvent = NativeTerminalInterop.CreateGapEvent(sourceSequence);
                return ApplyEventWithHostEffectsV2(in terminalEvent);
            }
        }

        public unsafe void ApplyBatch(params NativeTerminalBatchEvent[] events)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            lock (sync)
            {
                ThrowIfUnavailable();
                _ = ApplyBatchCore(events, captureHostEffects: false);
            }
        }

        public NativeTerminalHostEffectFrameV1 ApplyBatchWithHostEffectsV1(
            params NativeTerminalBatchEvent[] events)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            lock (sync)
            {
                ThrowIfUnavailable();
                return ApplyBatchCore(events, captureHostEffects: true);
            }
        }

        public NativeTerminalHostEffectFrameV2 ApplyBatchWithHostEffectsV2(
            params NativeTerminalBatchEvent[] events)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            lock (sync)
            {
                ThrowIfUnavailable();
                return ApplyBatchWithHostEffectsV2Core(events);
            }
        }

        public byte[] EncodeKey(
            NativeTerminalKey key,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            if (key == NativeTerminalKey.Character)
                throw new ArgumentException(
                    "Use EncodeCharacter for a Unicode character.",
                    nameof(key));
            lock (sync)
            {
                ThrowIfUnavailable();
                var input = NativeTerminalInterop.CreateKeyInput(key, modifiers, 0);
                return EncodeInput(in input);
            }
        }

        public byte[] EncodeCharacter(
            Rune character,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var input = NativeTerminalInterop.CreateKeyInput(
                    NativeTerminalKey.Character,
                    modifiers,
                    checked((uint)character.Value));
                return EncodeInput(in input);
            }
        }

        /// <summary>
        /// Encodes one lossless Win32 keyboard record. DEC private mode 9001
        /// must be enabled by terminal output before this method can succeed.
        /// </summary>
        public byte[] EncodeWin32Key(NativeTerminalWin32KeyEvent keyEvent)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var input = NativeTerminalInterop.CreateWin32KeyInput(keyEvent);
                return EncodeInputV2(in input);
            }
        }

        /// <summary>
        /// Attempts to encode one lossless Win32 record. A false result means that the
        /// terminal has not negotiated DEC private mode 9001; all other failures remain fatal.
        /// </summary>
        public bool TryEncodeWin32Key(
            NativeTerminalWin32KeyEvent keyEvent,
            out byte[] encoded)
        {
            try
            {
                encoded = EncodeWin32Key(keyEvent);
                return true;
            }
            catch (NativeTerminalException error)
                when (error.Status == NativeTerminalStatus.Unsupported)
            {
                encoded = null;
                return false;
            }
        }

        /// <summary>
        /// Encodes one semantic Kitty keyboard event according to the terminal's
        /// currently negotiated Kitty keyboard flags.
        /// </summary>
        public unsafe byte[] EncodeKittyKey(NativeTerminalKittyKeyEvent keyEvent)
        {
            keyEvent.Validate(nameof(keyEvent));
            var associatedText = keyEvent.EncodeAssociatedText();
            lock (sync)
            {
                ThrowIfUnavailable();
                fixed (byte* associatedTextPointer = associatedText)
                {
                    var input = NativeTerminalInterop.CreateKittyKeyInput(
                        keyEvent,
                        associatedText.Length == 0
                            ? IntPtr.Zero
                            : (IntPtr)associatedTextPointer,
                        checked((ulong)associatedText.LongLength));
                    return EncodeInputV3(in input);
                }
            }
        }

        public byte[] EncodePaste(byte[] paste)
        {
            if (paste == null) throw new ArgumentNullException(nameof(paste));
            return EncodePaste((ReadOnlySpan<byte>)paste);
        }

        public unsafe byte[] EncodePaste(ReadOnlySpan<byte> paste)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var ownedPaste = paste.ToArray();
                fixed (byte* pastePointer = ownedPaste)
                {
                    var input = NativeTerminalInterop.CreatePasteInput(
                        pastePointer,
                        checked((ulong)ownedPaste.LongLength));
                    return EncodeInput(in input);
                }
            }
        }

        public byte[] EncodeFocusGained()
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var input = NativeTerminalInterop.CreateFocusGainedInput();
                return EncodeInput(in input);
            }
        }

        public byte[] EncodeFocusLost()
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var input = NativeTerminalInterop.CreateFocusLostInput();
                return EncodeInput(in input);
            }
        }

        public byte[] EncodeMouse(NativeTerminalMouseEvent mouseEvent)
        {
            mouseEvent.Validate(nameof(mouseEvent));
            var input = NativeTerminalInterop.CreateMouseInput(mouseEvent);
            return EncodeMouseInput(in input);
        }

        public byte[] EncodeMousePress(
            NativeTerminalMouseButton button,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            return EncodeMouse(NativeTerminalMouseEvent.Press(
                button,
                buttonsHeld,
                position,
                modifiers));
        }

        public byte[] EncodeMouseRelease(
            NativeTerminalMouseButton button,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            return EncodeMouse(NativeTerminalMouseEvent.Release(
                button,
                buttonsHeld,
                position,
                modifiers));
        }

        public byte[] EncodeMouseMove(
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            return EncodeMouse(NativeTerminalMouseEvent.Move(
                buttonsHeld,
                position,
                modifiers));
        }

        public byte[] EncodeMouseWheel(
            NativeTerminalMouseWheelDirection direction,
            NativeTerminalMouseButtons buttonsHeld,
            NativeTerminalMousePosition position,
            NativeTerminalModifiers modifiers = NativeTerminalModifiers.None)
        {
            return EncodeMouse(NativeTerminalMouseEvent.Wheel(
                direction,
                buttonsHeld,
                position,
                modifiers));
        }

        public NativeTerminalCheckpoint CaptureCheckpoint()
        {
            if (TryCaptureCheckpoint(out var checkpoint, out var status)) return checkpoint;
            throw new NativeTerminalException(
                nameof(NativeTerminalInterop.TerminalCheckpointBegin),
                status);
        }

        /// <summary>
        /// Reads current presentation eligibility. Unlike mutating operations,
        /// this remains available after the terminal has failed closed.
        /// </summary>
        public NativeTerminalPresentationInfo GetPresentationInfo()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(NativeTerminal));
                if (brokerActive) throw new InvalidOperationException("A broker Output transaction owns this terminal.");

                var nativeInfo = NativeTerminalInterop.CreateEmptyPresentationInfoV1();
                var status = NativeTerminalInterop.TerminalGetPresentationInfoV1(
                    handle,
                    ref nativeInfo);
                if (status != NativeTerminalStatus.Ok)
                {
                    if (NativeTerminalInterop.RequiresTerminalRetirement(status))
                        failedClosed = true;
                    NativeTerminalInterop.ThrowIfFailed(
                        status,
                        nameof(NativeTerminalInterop.TerminalGetPresentationInfoV1));
                }

                if (nativeInfo.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    nativeInfo.StructSize != NativeTerminalInterop.PresentationInfoV1StructSize)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned incompatible presentation metadata.");

                const NativeTerminalPresentationFlags allFlags =
                    NativeTerminalPresentationFlags.FailedClosed |
                    NativeTerminalPresentationFlags.ParserIncomplete |
                    NativeTerminalPresentationFlags.SynchronizedOutput;
                var flags = (NativeTerminalPresentationFlags)nativeInfo.Flags;
                if ((flags & ~allFlags) != 0)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned unknown presentation flags.");
                if (nativeInfo.Reserved0 != 0 || nativeInfo.Reserved != 0)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned nonzero reserved presentation metadata.");

                var synchronized =
                    (flags & NativeTerminalPresentationFlags.SynchronizedOutput) != 0;
                if (synchronized != (nativeInfo.SynchronizedOutputDeadlineMicros != 0))
                    throw new NativeTerminalAbiException(
                        "The native terminal returned inconsistent synchronized-output metadata.");

                var nativeFailed =
                    (flags & NativeTerminalPresentationFlags.FailedClosed) != 0;
                if (nativeFailed != failedClosed)
                    throw new NativeTerminalAbiException(
                        "The native terminal failed-closed state diverged from its managed wrapper.");

                return new NativeTerminalPresentationInfo(
                    flags,
                    nativeInfo.NextSourceSequence,
                    nativeInfo.LogicalTimeMicros,
                    nativeInfo.SynchronizedOutputDeadlineMicros);
            }
        }

        public bool TryCaptureCheckpoint(
            out NativeTerminalCheckpoint checkpoint,
            out NativeTerminalStatus status)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var nativeHandle = IntPtr.Zero;
                status = NativeTerminalInterop.TerminalCheckpointBegin(
                    handle,
                    ref nativeHandle);
                if (status == NativeTerminalStatus.SnapshotNotEligible ||
                    status == NativeTerminalStatus.PresentationDeferred)
                {
                    NativeTerminalInterop.RequireEmptyFailedHandle(
                        nativeHandle,
                        nameof(NativeTerminalInterop.TerminalCheckpointBegin));
                    checkpoint = null;
                    return false;
                }

                if (status != NativeTerminalStatus.Ok)
                {
                    if (NativeTerminalInterop.RequiresTerminalRetirement(status))
                        failedClosed = true;
                    NativeTerminalInterop.RequireEmptyFailedHandle(
                        nativeHandle,
                        nameof(NativeTerminalInterop.TerminalCheckpointBegin));
                    throw new NativeTerminalException(
                        nameof(NativeTerminalInterop.TerminalCheckpointBegin),
                        status);
                }

                checkpoint = NativeTerminalCheckpoint.CreateOwned(
                    NativeTerminalInterop.OwnCheckpoint(nativeHandle), HasGraphicsProfile);
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                if (brokerActive) throw new InvalidOperationException("A broker Output transaction owns this terminal.");
                disposed = true;
                handle.Dispose();
            }
        }

        private string failureDiagnostic;

        private void ThrowApplyIfFailed(NativeTerminalStatus status, string operation)
        {
            if (status == NativeTerminalStatus.Ok) { failureDiagnostic = null; return; }
            failureDiagnostic = status == NativeTerminalStatus.FailedClosed
                ? ReadFailureDiagnostic() ?? failureDiagnostic
                : status == NativeTerminalStatus.Unsupported || status == NativeTerminalStatus.LimitExceeded
                    ? ReadFailureDiagnostic() : null;
            throw new NativeTerminalException(operation, status, failureDiagnostic);
        }

        private string ReadFailureDiagnostic()
        {
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                NativeTerminalStatus status;
                try { status = NativeTerminalInterop.TerminalExportFailureDiagnosticV3(handle, ref buffer); }
                catch (EntryPointNotFoundException) {
                    try { status = NativeTerminalInterop.TerminalExportFailureDiagnosticV2(handle, ref buffer); }
                    catch (EntryPointNotFoundException) { status = NativeTerminalInterop.TerminalExportFailureDiagnosticV1(handle, ref buffer); }
                }
                if (status != NativeTerminalStatus.Ok || buffer.Data == IntPtr.Zero ||
                    buffer.Length == 0 || buffer.Length > 8192 ||
                    buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize) return null;
                var bytes = new byte[(int)buffer.Length];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                foreach (var value in bytes)
                    if (value != 10 && (value < 32 || value > 126)) return null;
                var text = Encoding.ASCII.GetString(bytes);
                return text.StartsWith("TL-DIAG/3\n", StringComparison.Ordinal) || (text.Length <= 4096 &&
                    (text.StartsWith("TL-DIAG/1\n", StringComparison.Ordinal) || text.StartsWith("TL-DIAG/2\n", StringComparison.Ordinal))) ? text : null;
            }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (buffer.Data != IntPtr.Zero)
                    NativeTerminalInterop.TerminalBufferFree(ref buffer);
            }
        }

        private void ApplyEvent(in TlTerminalEvent terminalEvent)
        {
            var status = NativeTerminalInterop.TerminalApplyEvent(handle, in terminalEvent);
            if (status != NativeTerminalStatus.Ok &&
                NativeTerminalInterop.RequiresTerminalRetirement(status))
                failedClosed = true;
            ThrowApplyIfFailed(
                status,
                nameof(NativeTerminalInterop.TerminalApplyEvent));
        }

        private NativeTerminalHostEffectFrameV1 ApplyEventWithHostEffectsV1(
            in TlTerminalEvent terminalEvent)
        {
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            NativeTerminalStatus status;
            try
            {
                status = NativeTerminalInterop.TerminalApplyEventWithHostEffectsV1(
                    handle,
                    in terminalEvent,
                    ref buffer);
            }
            catch (Exception error)
            {
                failedClosed = true;
                var releaseError = HasOwnedBufferMetadata(in buffer)
                    ? TryReleaseHostEffectBuffer(ref buffer)
                    : null;
                if (releaseError != null)
                    throw new NativeTerminalAbiException(
                        nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV1) +
                        " threw after publishing a host effect buffer." +
                        ReleaseFailureSuffix(releaseError),
                        error);
                throw;
            }
            return CompleteHostEffectApply(
                status,
                ref buffer,
                nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV1));
        }

        private NativeTerminalHostEffectFrameV2 ApplyEventWithHostEffectsV2(
            in TlTerminalEvent terminalEvent)
        {
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            NativeTerminalStatus status;
            try
            {
                status = NativeTerminalInterop.TerminalApplyEventWithHostEffectsV2(
                    handle,
                    in terminalEvent,
                    ref buffer);
            }
            catch (Exception error)
            {
                failedClosed = true;
                var releaseError = HasOwnedBufferMetadata(in buffer)
                    ? TryReleaseHostEffectBuffer(ref buffer)
                    : null;
                if (releaseError != null)
                    throw new NativeTerminalAbiException(
                        nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV2) +
                        " threw after publishing a host effect buffer." +
                        ReleaseFailureSuffix(releaseError),
                        error);
                throw;
            }
            return CompleteHostEffectApplyV2(
                status,
                ref buffer,
                nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV2));
        }

        private unsafe NativeTerminalHostEffectFrameV1 ApplyBatchCore(
            NativeTerminalBatchEvent[] events,
            bool captureHostEffects)
        {
            ValidateBatch(events);

            var nativeEvents = new TlTerminalEvent[events.Length];
            var payloadPins = new GCHandle[events.Length];
            var payloadPinCount = 0;
            try
            {
                for (var index = 0; index < events.Length; index++)
                {
                    var batchEvent = events[index];
                    switch (batchEvent.Kind)
                    {
                        case NativeTerminalBatchEventKind.Output:
                        case NativeTerminalBatchEventKind.HostGeometryV1:
                        case NativeTerminalBatchEventKind.HostThemeV1:
                            payloadPins[payloadPinCount] = GCHandle.Alloc(
                                batchEvent.OutputBytes,
                                GCHandleType.Pinned);
                            payloadPinCount++;
                            var outputPointer = (byte*)payloadPins[payloadPinCount - 1]
                                .AddrOfPinnedObject();
                            nativeEvents[index] = NativeTerminalInterop.CreateOutputEvent(
                                batchEvent.SourceSequence,
                                outputPointer,
                                checked((ulong)batchEvent.OutputBytes.LongLength));
                            nativeEvents[index].Kind = (uint)batchEvent.Kind;
                            break;
                        case NativeTerminalBatchEventKind.Resize:
                            nativeEvents[index] = NativeTerminalInterop.CreateResizeEvent(
                                batchEvent.SourceSequence,
                                batchEvent.Columns,
                                batchEvent.Rows);
                            break;
                        case NativeTerminalBatchEventKind.Advance:
                            nativeEvents[index] = NativeTerminalInterop.CreateAdvanceEvent(
                                batchEvent.SourceSequence,
                                batchEvent.AdvanceMicros);
                            break;
                        case NativeTerminalBatchEventKind.Gap:
                            nativeEvents[index] = NativeTerminalInterop.CreateGapEvent(
                                batchEvent.SourceSequence);
                            break;
                        default:
                            throw new InvalidOperationException(
                                "A validated batch event had an unknown kind.");
                    }
                }

                fixed (TlTerminalEvent* eventPointer = nativeEvents)
                {
                    if (captureHostEffects)
                    {
                        var buffer = NativeTerminalInterop.CreateEmptyBuffer();
                        NativeTerminalStatus status;
                        try
                        {
                            status = NativeTerminalInterop
                                .TerminalApplyEventBatchWithHostEffectsV1(
                                    handle,
                                    eventPointer,
                                    checked((uint)nativeEvents.Length),
                                    ref buffer);
                        }
                        catch (Exception error)
                        {
                            failedClosed = true;
                            var releaseError = HasOwnedBufferMetadata(in buffer)
                                ? TryReleaseHostEffectBuffer(ref buffer)
                                : null;
                            if (releaseError != null)
                                throw new NativeTerminalAbiException(
                                    nameof(NativeTerminalInterop
                                        .TerminalApplyEventBatchWithHostEffectsV1) +
                                    " threw after publishing a host effect buffer." +
                                    ReleaseFailureSuffix(releaseError),
                                    error);
                            throw;
                        }
                        return CompleteHostEffectApply(
                            status,
                            ref buffer,
                            nameof(NativeTerminalInterop
                                .TerminalApplyEventBatchWithHostEffectsV1));
                    }

                    var legacyStatus = NativeTerminalInterop.TerminalApplyEventBatchV1(
                        handle,
                        eventPointer,
                        checked((uint)nativeEvents.Length));
                    if (legacyStatus != NativeTerminalStatus.Ok &&
                        NativeTerminalInterop.RequiresTerminalRetirement(legacyStatus))
                        failedClosed = true;
                    ThrowApplyIfFailed(
                        legacyStatus,
                        nameof(NativeTerminalInterop.TerminalApplyEventBatchV1));
                    return null;
                }
            }
            finally
            {
                for (var index = payloadPinCount - 1; index >= 0; index--)
                    payloadPins[index].Free();
            }
        }

        private unsafe NativeTerminalHostEffectFrameV2 ApplyBatchWithHostEffectsV2Core(
            NativeTerminalBatchEvent[] events)
        {
            ValidateBatch(events);

            var nativeEvents = new TlTerminalEvent[events.Length];
            var payloadPins = new GCHandle[events.Length];
            var payloadPinCount = 0;
            try
            {
                for (var index = 0; index < events.Length; index++)
                {
                    var batchEvent = events[index];
                    switch (batchEvent.Kind)
                    {
                        case NativeTerminalBatchEventKind.Output:
                        case NativeTerminalBatchEventKind.HostGeometryV1:
                        case NativeTerminalBatchEventKind.HostThemeV1:
                            payloadPins[payloadPinCount] = GCHandle.Alloc(
                                batchEvent.OutputBytes,
                                GCHandleType.Pinned);
                            payloadPinCount++;
                            var outputPointer = (byte*)payloadPins[payloadPinCount - 1]
                                .AddrOfPinnedObject();
                            nativeEvents[index] = NativeTerminalInterop.CreateOutputEvent(
                                batchEvent.SourceSequence,
                                outputPointer,
                                checked((ulong)batchEvent.OutputBytes.LongLength));
                            nativeEvents[index].Kind = (uint)batchEvent.Kind;
                            break;
                        case NativeTerminalBatchEventKind.Resize:
                            nativeEvents[index] = NativeTerminalInterop.CreateResizeEvent(
                                batchEvent.SourceSequence,
                                batchEvent.Columns,
                                batchEvent.Rows);
                            break;
                        case NativeTerminalBatchEventKind.Advance:
                            nativeEvents[index] = NativeTerminalInterop.CreateAdvanceEvent(
                                batchEvent.SourceSequence,
                                batchEvent.AdvanceMicros);
                            break;
                        case NativeTerminalBatchEventKind.Gap:
                            nativeEvents[index] = NativeTerminalInterop.CreateGapEvent(
                                batchEvent.SourceSequence);
                            break;
                        default:
                            throw new InvalidOperationException(
                                "A validated batch event had an unknown kind.");
                    }
                }

                fixed (TlTerminalEvent* eventPointer = nativeEvents)
                {
                    var buffer = NativeTerminalInterop.CreateEmptyBuffer();
                    NativeTerminalStatus status;
                    try
                    {
                        status = NativeTerminalInterop
                            .TerminalApplyEventBatchWithHostEffectsV2(
                                handle,
                                eventPointer,
                                checked((uint)nativeEvents.Length),
                                ref buffer);
                    }
                    catch (Exception error)
                    {
                        failedClosed = true;
                        var releaseError = HasOwnedBufferMetadata(in buffer)
                            ? TryReleaseHostEffectBuffer(ref buffer)
                            : null;
                        if (releaseError != null)
                            throw new NativeTerminalAbiException(
                                nameof(NativeTerminalInterop
                                    .TerminalApplyEventBatchWithHostEffectsV2) +
                                " threw after publishing a host effect buffer." +
                                ReleaseFailureSuffix(releaseError),
                                error);
                        throw;
                    }
                    return CompleteHostEffectApplyV2(
                        status,
                        ref buffer,
                        nameof(NativeTerminalInterop
                            .TerminalApplyEventBatchWithHostEffectsV2));
                }
            }
            finally
            {
                for (var index = payloadPinCount - 1; index >= 0; index--)
                    payloadPins[index].Free();
            }
        }

        private NativeTerminalHostEffectFrameV1 CompleteHostEffectApply(
            NativeTerminalStatus status,
            ref TlTerminalBuffer buffer,
            string operation)
        {
            if (status != NativeTerminalStatus.Ok)
            {
                if (NativeTerminalInterop.RequiresTerminalRetirement(status))
                    failedClosed = true;
                if (HasOwnedBufferMetadata(in buffer))
                {
                    failedClosed = true;
                    var releaseError = TryReleaseHostEffectBuffer(ref buffer);
                    throw new NativeTerminalAbiException(
                        operation + " returned a non-empty host effect buffer after status " +
                        status.ToString() + " (" + ((uint)status).ToString() + ")." +
                        ReleaseFailureSuffix(releaseError),
                        releaseError);
                }
                ThrowApplyIfFailed(status, operation);
            }

            byte[] bytes = null;
            Exception copyError = null;
            try
            {
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero ||
                    buffer.Length < 64 ||
                    buffer.Length >
                        (ulong)NativeTerminalHostEffectFrameV1Decoder.MaximumFrameBytes ||
                    buffer.Length > int.MaxValue)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned an invalid host effect frame buffer.");
                bytes = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            }
            catch (Exception error)
            {
                copyError = error;
            }

            var freeError = HasOwnedBufferMetadata(in buffer)
                ? TryReleaseHostEffectBuffer(ref buffer)
                : null;
            if (copyError != null || freeError != null)
            {
                failedClosed = true;
                if (copyError is NativeTerminalAbiException && freeError == null)
                    throw copyError;
                throw new NativeTerminalAbiException(
                    operation + " could not safely copy and release its host effect buffer." +
                    ReleaseFailureSuffix(freeError),
                    copyError ?? freeError);
            }

            try
            {
                return NativeTerminalHostEffectFrameV1Decoder.Decode(bytes);
            }
            catch
            {
                failedClosed = true;
                throw;
            }
        }

        private NativeTerminalHostEffectFrameV2 CompleteHostEffectApplyV2(
            NativeTerminalStatus status,
            ref TlTerminalBuffer buffer,
            string operation)
        {
            if (status != NativeTerminalStatus.Ok)
            {
                if (NativeTerminalInterop.RequiresTerminalRetirement(status))
                    failedClosed = true;
                if (HasOwnedBufferMetadata(in buffer))
                {
                    failedClosed = true;
                    var releaseError = TryReleaseHostEffectBuffer(ref buffer);
                    throw new NativeTerminalAbiException(
                        operation + " returned a non-empty host effect buffer after status " +
                        status.ToString() + " (" + ((uint)status).ToString() + ")." +
                        ReleaseFailureSuffix(releaseError),
                        releaseError);
                }
                ThrowApplyIfFailed(status, operation);
            }

            byte[] bytes = null;
            Exception copyError = null;
            try
            {
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero ||
                    buffer.Length < 64 ||
                    buffer.Length >
                        (ulong)NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes ||
                    buffer.Length > int.MaxValue)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned an invalid host effect frame v2 buffer.");
                bytes = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            }
            catch (Exception error)
            {
                copyError = error;
            }

            var freeError = HasOwnedBufferMetadata(in buffer)
                ? TryReleaseHostEffectBuffer(ref buffer)
                : null;
            if (copyError != null || freeError != null)
            {
                failedClosed = true;
                if (copyError is NativeTerminalAbiException && freeError == null)
                    throw copyError;
                throw new NativeTerminalAbiException(
                    operation +
                    " could not safely copy and release its host effect frame v2 buffer." +
                    ReleaseFailureSuffix(freeError),
                    copyError ?? freeError);
            }

            try
            {
                return NativeTerminalHostEffectFrameV2Decoder.Decode(bytes);
            }
            catch
            {
                failedClosed = true;
                throw;
            }
        }

        private static bool HasOwnedBufferMetadata(in TlTerminalBuffer buffer)
        {
            return buffer.Data != IntPtr.Zero || buffer.Length != 0;
        }

        private static Exception TryReleaseHostEffectBuffer(ref TlTerminalBuffer buffer)
        {
            try
            {
                var status = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                if (status != NativeTerminalStatus.Ok)
                    return new NativeTerminalException(
                        nameof(NativeTerminalInterop.TerminalBufferFree),
                        status);
                if (HasOwnedBufferMetadata(in buffer))
                    return new NativeTerminalAbiException(
                        "The native terminal did not clear a released host effect buffer.");
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static string ReleaseFailureSuffix(Exception releaseError)
        {
            return releaseError == null
                ? string.Empty
                : " Buffer release also failed: " + releaseError.Message;
        }

        private static void ValidateBatch(NativeTerminalBatchEvent[] events)
        {
            if (events.Length == 0 || events.Length > MaxBatchEventCount)
                throw new ArgumentOutOfRangeException(
                    nameof(events),
                    "A batch must contain 1 through 256 events.");

            var totalOutputBytes = 0L;
            for (var index = 0; index < events.Length; index++)
            {
                var batchEvent = events[index];
                if (batchEvent == null)
                    throw new ArgumentException(
                        "A batch cannot contain a null event.",
                        nameof(events));
                if (index != 0)
                {
                    var previousSequence = events[index - 1].SourceSequence;
                    if (previousSequence == ulong.MaxValue ||
                        batchEvent.SourceSequence != previousSequence + 1UL)
                        throw new ArgumentException(
                            "Batch source sequences must be contiguous.",
                            nameof(events));
                }

                switch (batchEvent.Kind)
                {
                    case NativeTerminalBatchEventKind.Output:
                        if (batchEvent.OutputBytes == null ||
                            batchEvent.OutputBytes.Length == 0 ||
                            batchEvent.OutputBytes.Length > NativeTerminalBatchEvent.MaxOutputBytes)
                            throw new ArgumentException(
                                "A batch output payload is outside its supported bound.",
                                nameof(events));
                        totalOutputBytes = checked(
                            totalOutputBytes + batchEvent.OutputBytes.LongLength);
                        if (totalOutputBytes > MaxBatchOutputBytes)
                            throw new ArgumentOutOfRangeException(
                                nameof(events),
                                "Batch output payloads exceed 4,194,304 total bytes.");
                        break;
                    case NativeTerminalBatchEventKind.HostThemeV1:
                        NativeTerminalHostThemeV1.Decode(batchEvent.OutputBytes);
                        break;
                    case NativeTerminalBatchEventKind.HostGeometryV1:
                        NativeTerminalHostGeometryV1.Decode(batchEvent.OutputBytes);
                        break;
                    case NativeTerminalBatchEventKind.Resize:
                    case NativeTerminalBatchEventKind.Advance:
                    case NativeTerminalBatchEventKind.Gap:
                        break;
                    default:
                        throw new ArgumentException(
                            "A batch contains an unknown event kind.",
                            nameof(events));
                }
            }
        }

        private byte[] EncodeInput(in TlTerminalInputV1 input)
        {
            var encoded = NativeTerminalInterop.CreateEmptyBuffer();
            var diagnostic = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = NativeTerminalInterop.TerminalEncodeInputV1WithDiagnostic(
                    handle, in input, ref encoded, ref diagnostic);
                return CompleteInputEncoding(status, ref encoded, ref diagnostic,
                    nameof(NativeTerminalInterop.TerminalEncodeInputV1WithDiagnostic));
            }
            catch (EntryPointNotFoundException error)
            {
                throw MissingInputDiagnosticAbi(error);
            }
            finally
            {
                ReleaseInputBuffers(ref encoded, ref diagnostic);
            }
        }

        private byte[] EncodeInputV2(in TlTerminalInputV2 input)
        {
            var encoded = NativeTerminalInterop.CreateEmptyBuffer();
            var diagnostic = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = NativeTerminalInterop.TerminalEncodeInputV2WithDiagnostic(
                    handle, in input, ref encoded, ref diagnostic);
                return CompleteInputEncoding(status, ref encoded, ref diagnostic,
                    nameof(NativeTerminalInterop.TerminalEncodeInputV2WithDiagnostic));
            }
            catch (EntryPointNotFoundException error)
            {
                throw MissingInputDiagnosticAbi(error);
            }
            finally
            {
                ReleaseInputBuffers(ref encoded, ref diagnostic);
            }
        }

        private byte[] EncodeInputV3(in TlTerminalInputV3 input)
        {
            var encoded = NativeTerminalInterop.CreateEmptyBuffer();
            var diagnostic = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = NativeTerminalInterop.TerminalEncodeInputV3WithDiagnostic(
                    handle, in input, ref encoded, ref diagnostic);
                return CompleteInputEncoding(status, ref encoded, ref diagnostic,
                    nameof(NativeTerminalInterop.TerminalEncodeInputV3WithDiagnostic));
            }
            catch (EntryPointNotFoundException error)
            {
                throw MissingInputDiagnosticAbi(error);
            }
            finally
            {
                ReleaseInputBuffers(ref encoded, ref diagnostic);
            }
        }

        internal byte[] EncodeMouseInput(in TlTerminalMouseInputV1 input)
        {
            lock (sync)
            {
                ThrowIfUnavailable();
                var encoded = NativeTerminalInterop.CreateEmptyBuffer();
                var diagnostic = NativeTerminalInterop.CreateEmptyBuffer();
                try
                {
                    var status = NativeTerminalInterop.TerminalEncodeMouseInputV1WithDiagnostic(
                        handle, in input, ref encoded, ref diagnostic);
                    return CompleteInputEncoding(status, ref encoded, ref diagnostic,
                        nameof(NativeTerminalInterop.TerminalEncodeMouseInputV1WithDiagnostic));
                }
                catch (EntryPointNotFoundException error)
                {
                    throw MissingInputDiagnosticAbi(error);
                }
                finally
                {
                    ReleaseInputBuffers(ref encoded, ref diagnostic);
                }
            }
        }

        private byte[] CompleteInputEncoding(NativeTerminalStatus status,
            ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic, string operation)
        {
            if (status != NativeTerminalStatus.Ok &&
                NativeTerminalInterop.RequiresTerminalRetirementAfterInput(status))
                failedClosed = true;
            if (encoded.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                encoded.StructSize != NativeTerminalInterop.BufferStructSize ||
                diagnostic.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                diagnostic.StructSize != NativeTerminalInterop.BufferStructSize)
                throw new NativeTerminalAbiException("The native terminal returned an invalid input buffer header.");

            if (status != NativeTerminalStatus.Ok)
            {
                if (encoded.Data != IntPtr.Zero || encoded.Length != 0)
                    throw new NativeTerminalAbiException("The native terminal encoded bytes after input failure.");
                throw new NativeTerminalException(operation, status, ReadInputDiagnostic(in diagnostic));
            }
            if (diagnostic.Data != IntPtr.Zero || diagnostic.Length != 0 ||
                encoded.Data == IntPtr.Zero || encoded.Length > int.MaxValue)
                throw new NativeTerminalAbiException("The native terminal returned invalid encoded input.");
            var bytes = encoded.Length == 0
                ? Array.Empty<byte>() : new byte[checked((int)encoded.Length)];
            if (bytes.Length != 0)
                Marshal.Copy(encoded.Data, bytes, 0, bytes.Length);
            return bytes;
        }

        private static string ReadInputDiagnostic(in TlTerminalBuffer diagnostic)
        {
            if (diagnostic.Data == IntPtr.Zero && diagnostic.Length == 0) return null;
            if (diagnostic.Data == IntPtr.Zero || diagnostic.Length == 0 || diagnostic.Length > 2048)
                throw new NativeTerminalAbiException("The native terminal returned an invalid input diagnostic buffer.");
            var bytes = new byte[checked((int)diagnostic.Length)];
            Marshal.Copy(diagnostic.Data, bytes, 0, bytes.Length);
            foreach (var value in bytes)
                if (value != 10 && (value < 32 || value > 126))
                    throw new NativeTerminalAbiException("The native terminal returned non-ASCII input diagnostic data.");
            var text = Encoding.ASCII.GetString(bytes);
            if (!text.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n", StringComparison.Ordinal) ||
                text[text.Length - 1] != '\n')
                throw new NativeTerminalAbiException("The native terminal returned an invalid input diagnostic format.");
            return text;
        }

        private static void ReleaseInputBuffers(ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic)
        {
            try { ReleaseInputBuffer(ref encoded); }
            finally { ReleaseInputBuffer(ref diagnostic); }
        }

        private static void ReleaseInputBuffer(ref TlTerminalBuffer buffer)
        {
            if (buffer.Data == IntPtr.Zero && buffer.Length == 0) return;
            var freeStatus = NativeTerminalInterop.TerminalBufferFree(ref buffer);
            NativeTerminalInterop.ThrowIfFailed(freeStatus,
                nameof(NativeTerminalInterop.TerminalBufferFree));
            if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                throw new NativeTerminalAbiException("The native terminal did not clear a released input buffer.");
        }

        private static NativeTerminalAbiException MissingInputDiagnosticAbi(EntryPointNotFoundException error)
        {
            return new NativeTerminalAbiException(
                "The installed terminal core DLL does not support input diagnostics; upgrade the native binary.",
                error);
        }

        private void ThrowIfUnavailable()
        {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminal));
            if (brokerActive) throw new InvalidOperationException("A broker Output transaction owns this terminal.");
            if (failedClosed)
                throw new NativeTerminalException(
                    nameof(NativeTerminal),
                    NativeTerminalStatus.FailedClosed, failureDiagnostic);
        }
    }

    public sealed partial class NativeTerminalCheckpoint : IDisposable
    {
        private readonly object sync = new object();
        private readonly SafeTlTerminalCheckpointHandle handle;
        private readonly NativeTerminalCheckpointInfo info;
        private bool disposed;

        private NativeTerminalCheckpoint(SafeTlTerminalCheckpointHandle handle, bool hasGraphicsProfile)
        {
            this.handle = handle;
            HasGraphicsProfile = hasGraphicsProfile;
            info = ReadInfo();
        }

        public bool HasGraphicsProfile { get; }

        internal static NativeTerminalCheckpoint CreateOwned(
            SafeTlTerminalCheckpointHandle handle, bool hasGraphicsProfile = false)
        {
            try
            {
                return new NativeTerminalCheckpoint(handle, hasGraphicsProfile);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public NativeTerminalCheckpointInfo Info
        {
            get
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    return info;
                }
            }
        }

        public byte[] ExportSnapshot()
        {
            return ExportSnapshot(
                (source, destination, length) =>
                    Marshal.Copy(source, destination, 0, length));
        }

        internal byte[] ExportSnapshot(Action<IntPtr, byte[], int> copy)
        {
            if (copy == null) throw new ArgumentNullException(nameof(copy));
            lock (sync)
            {
                ThrowIfDisposed();
                RequireTextProfile();
                return ExportBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportSnapshot,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportSnapshot),
                    copy, NativeTerminalCoreV2.MaximumSnapshotBytes);
            }
        }

        public byte[] ExportGraphicsSnapshot()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                RequireGraphicsProfile();
                return ExportBuffer(NativeTerminalInterop.TerminalCheckpointExportSnapshotGraphicsV1,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportSnapshotGraphicsV1),
                    (source, destination, length) => Marshal.Copy(source, destination, 0, length),
                    NativeTerminalCoreV2.MaximumGraphicsSnapshotBytes);
            }
        }

        public byte[] ExportDelta()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return ExportBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportDelta,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportDelta),
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrame(ulong maxBytes = 16UL * 1024UL * 1024UL)
        {
            return ExportRenderFrameV1(maxBytes);
        }

        public byte[] ExportRenderFrameV1(ulong maxBytes = 16UL * 1024UL * 1024UL)
        {
            if (maxBytes == 0 || maxBytes > 16UL * 1024UL * 1024UL)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                return ExportRenderFrameBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportRenderFrameV1,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV1),
                    maxBytes,
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrameV2(ulong maxBytes = 16UL * 1024UL * 1024UL)
        {
            if (maxBytes == 0 || maxBytes > 16UL * 1024UL * 1024UL)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                return ExportRenderFrameBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportRenderFrameV2,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV2),
                    maxBytes,
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrameV3(ulong maxBytes = 16UL * 1024UL * 1024UL)
        {
            if (maxBytes == 0 || maxBytes > 16UL * 1024UL * 1024UL)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                return ExportRenderFrameBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportRenderFrameV3,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV3),
                    maxBytes,
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public NativeTerminalShellState ShellState() => NativeTerminalShellState.Decode(ExportShellStateV1(), Info.NextSourceSequence);

        public byte[] ExportShellStateV1(ulong maxBytes = NativeTerminalShellState.MaximumBytes)
        {
            if (maxBytes == 0 || maxBytes > NativeTerminalShellState.MaximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                return ExportRenderFrameBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportShellStateV1,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportShellStateV1),
                    maxBytes,
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrameV4(ulong maxBytes = 32UL * 1024UL * 1024UL)
        {
            if (maxBytes == 0 || maxBytes > 32UL * 1024UL * 1024UL)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                NativeTerminalInterop.RequireV4Abi();
                RequireTextProfile();
                return ExportRenderFrameBuffer(
                    NativeTerminalInterop.TerminalCheckpointExportRenderFrameV4,
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV4),
                    maxBytes,
                    (source, destination, length) =>
                        Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrameV4AtScrollback(uint offsetRows, ulong maxBytes = 32UL * 1024UL * 1024UL)
        {
            if (offsetRows > 10_000) throw new ArgumentOutOfRangeException(nameof(offsetRows));
            if (maxBytes == 0 || maxBytes > 32UL * 1024UL * 1024UL) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            lock (sync)
            {
                ThrowIfDisposed();
                NativeTerminalInterop.RequireV4Abi();
                RequireTextProfile();
                return ExportRenderFrameBuffer(
                    (SafeTlTerminalCheckpointHandle checkpoint, ulong maximum, ref TlTerminalBuffer output) =>
                        NativeTerminalInterop.TerminalCheckpointExportRenderFrameV4AtScrollback(checkpoint, offsetRows, maximum, ref output),
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV4AtScrollback), maxBytes,
                    (source, destination, length) => Marshal.Copy(source, destination, 0, length));
            }
        }

        public byte[] ExportRenderFrameV5(uint flags = 0)
        {
            if (flags != 0) throw new ArgumentOutOfRangeException(nameof(flags));
            lock (sync)
            {
                ThrowIfDisposed();
                RequireGraphicsProfile();
                return ExportBuffer(
                    (SafeTlTerminalCheckpointHandle checkpoint, ref TlTerminalBuffer output) =>
                        NativeTerminalInterop.TerminalCheckpointExportRenderFrameV5(checkpoint, flags, ref output),
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV5),
                    (source, destination, length) => Marshal.Copy(source, destination, 0, length),
                    NativeTerminalRenderFrameV5Decoder.MaximumFrameBytes);
            }
        }

        public byte[] ExportRenderFrameV5AtScrollback(uint offsetRows, uint flags = 0)
        {
            if (offsetRows > 10_000) throw new ArgumentOutOfRangeException(nameof(offsetRows));
            if (flags != 0) throw new ArgumentOutOfRangeException(nameof(flags));
            lock (sync)
            {
                ThrowIfDisposed();
                RequireGraphicsProfile();
                return ExportBuffer(
                    (SafeTlTerminalCheckpointHandle checkpoint, ref TlTerminalBuffer output) =>
                        NativeTerminalInterop.TerminalCheckpointExportRenderFrameV5AtScrollback(checkpoint, offsetRows, flags, ref output),
                    nameof(NativeTerminalInterop.TerminalCheckpointExportRenderFrameV5AtScrollback),
                    (source, destination, length) => Marshal.Copy(source, destination, 0, length),
                    NativeTerminalRenderFrameV5Decoder.MaximumFrameBytes);
            }
        }

        public NativeTerminalRenderFrameV5 RenderFrameV5() => NativeTerminalRenderFrameV5Decoder.Decode(ExportRenderFrameV5());
        public NativeTerminalRenderFrameV5 RenderFrameV5AtScrollback(uint offsetRows) =>
            NativeTerminalRenderFrameV5Decoder.Decode(ExportRenderFrameV5AtScrollback(offsetRows));

        private void RequireGraphicsProfile()
        {
            if (!HasGraphicsProfile) throw new InvalidOperationException("This checkpoint requires an explicitly enabled graphics profile.");
            NativeTerminalInterop.RequireGraphicsV1Abi();
        }

        private void RequireTextProfile()
        {
            if (HasGraphicsProfile) throw new InvalidOperationException("A graphics checkpoint requires the graphics snapshot or Render V5 API.");
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                handle.Dispose();
            }
        }

        private NativeTerminalCheckpointInfo ReadInfo()
        {
            var nativeInfo = NativeTerminalInterop.CreateEmptyCheckpointInfo();
            var status = NativeTerminalInterop.TerminalCheckpointGetInfo(handle, ref nativeInfo);
            NativeTerminalInterop.ThrowIfFailed(
                status,
                nameof(NativeTerminalInterop.TerminalCheckpointGetInfo));
            if (nativeInfo.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                nativeInfo.StructSize != NativeTerminalInterop.CheckpointInfoStructSize ||
                nativeInfo.Reserved0 != 0 ||
                nativeInfo.Reserved1 != 0)
                throw new NativeTerminalAbiException(
                    "The native terminal returned invalid checkpoint metadata.");
            return new NativeTerminalCheckpointInfo(
                nativeInfo.Revision,
                nativeInfo.NextSourceSequence);
        }

        private byte[] ExportBuffer(
            NativeTerminalInterop.CheckpointBufferExporter exporter,
            string operation,
            Action<IntPtr, byte[], int> copy,
            ulong maxBytes = int.MaxValue)
        {
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = exporter(handle, ref buffer);
                NativeTerminalInterop.ThrowIfFailed(status, operation);
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero ||
                    buffer.Length == 0 ||
                    buffer.Length > maxBytes ||
                    buffer.Length > int.MaxValue)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned an invalid owned buffer.");
                var bytes = new byte[checked((int)buffer.Length)];
                copy(buffer.Data, bytes, bytes.Length);
                return bytes;
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                {
                    var freeStatus = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                    NativeTerminalInterop.ThrowIfFailed(
                        freeStatus,
                        nameof(NativeTerminalInterop.TerminalBufferFree));
                    if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                        throw new NativeTerminalAbiException(
                            "The native terminal did not clear a released owned buffer.");
                }
            }
        }

        private byte[] ExportRenderFrameBuffer(
            NativeTerminalInterop.CheckpointRenderFrameExporter exporter,
            string operation,
            ulong maxBytes,
            Action<IntPtr, byte[], int> copy)
        {
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = exporter(
                    handle,
                    maxBytes,
                    ref buffer);
                NativeTerminalInterop.ThrowIfFailed(status, operation);
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero ||
                    buffer.Length == 0 ||
                    buffer.Length > maxBytes ||
                    buffer.Length > int.MaxValue)
                    throw new NativeTerminalAbiException(
                        "The native terminal returned an invalid render frame buffer.");
                var bytes = new byte[checked((int)buffer.Length)];
                copy(buffer.Data, bytes, bytes.Length);
                return bytes;
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                {
                    var freeStatus = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                    NativeTerminalInterop.ThrowIfFailed(
                        freeStatus,
                        nameof(NativeTerminalInterop.TerminalBufferFree));
                    if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                        throw new NativeTerminalAbiException(
                            "The native terminal did not clear a released render frame buffer.");
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalCheckpoint));
        }
    }

    internal sealed class SafeTlTerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeTlTerminalHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            NativeTerminalInterop.TerminalDestroy(handle);
            return true;
        }
    }

    internal sealed class SafeTlTerminalCheckpointHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeTlTerminalCheckpointHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            NativeTerminalInterop.TerminalCheckpointDestroy(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TlTerminalConfigV1
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Columns;
        internal uint Rows;
        internal ulong InitialSourceSequence;
        internal fixed ulong Reserved[2];
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct TlTerminalConfigV2
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Columns;
        [FieldOffset(12)] internal uint Rows;
        [FieldOffset(16)] internal ulong InitialSourceSequence;
        [FieldOffset(24)] internal ulong ReservedV1Compat0;
        [FieldOffset(32)] internal ulong ReservedV1Compat1;
        [FieldOffset(40)] internal ulong HistoryCapacityRows;
        [FieldOffset(48)] internal ulong Reserved0;
        [FieldOffset(56)] internal ulong Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 88)]
    internal struct TlTerminalConfigV3
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Columns;
        [FieldOffset(12)] internal uint Rows;
        [FieldOffset(16)] internal ulong InitialSourceSequence;
        [FieldOffset(24)] internal ulong ReservedV1Compat0;
        [FieldOffset(32)] internal ulong ReservedV1Compat1;
        [FieldOffset(40)] internal ulong HistoryCapacityRows;
        [FieldOffset(48)] internal ulong ReservedV2Compat0;
        [FieldOffset(56)] internal ulong ReservedV2Compat1;
        [FieldOffset(64)] internal uint OutputProfile;
        [FieldOffset(68)] internal uint ReservedV3Padding;
        [FieldOffset(72)] internal ulong ReservedV3Tail0;
        [FieldOffset(80)] internal ulong ReservedV3Tail1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    internal struct TlTerminalConfigV4
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Columns;
        [FieldOffset(12)] internal uint Rows;
        [FieldOffset(16)] internal ulong InitialSourceSequence;
        [FieldOffset(24)] internal ulong ReservedV1Compat0;
        [FieldOffset(32)] internal ulong ReservedV1Compat1;
        [FieldOffset(40)] internal ulong HistoryCapacityRows;
        [FieldOffset(48)] internal ulong ReservedV2Compat0;
        [FieldOffset(56)] internal ulong ReservedV2Compat1;
        [FieldOffset(64)] internal uint OutputProfile;
        [FieldOffset(68)] internal uint ReservedV3Padding;
        [FieldOffset(72)] internal ulong ReservedV3Tail0;
        [FieldOffset(80)] internal ulong ReservedV3Tail1;
        [FieldOffset(88)] internal uint WidthProfile;
        [FieldOffset(92)] internal uint ReservedV4Padding;
        [FieldOffset(96)] internal ulong ReservedV4Tail0;
        [FieldOffset(104)] internal ulong ReservedV4Tail1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TlTerminalEvent
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong SourceSequence;
        internal uint Kind;
        internal uint Reserved;
        internal IntPtr Data;
        internal ulong DataLength;
        internal uint Columns;
        internal uint Rows;
        internal ulong AdvanceMicros;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TlTerminalInputV1
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Kind;
        internal uint Key;
        internal uint Modifiers;
        internal uint Character;
        internal IntPtr Data;
        internal ulong DataLength;
        internal ulong Reserved0;
        internal ulong Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    internal struct TlTerminalInputV2
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Kind;
        [FieldOffset(12)] internal uint Key;
        [FieldOffset(16)] internal uint Modifiers;
        [FieldOffset(20)] internal uint Character;
        [FieldOffset(24)] internal IntPtr Data;
        [FieldOffset(32)] internal ulong DataLength;
        [FieldOffset(40)] internal ulong ReservedV1Compat0;
        [FieldOffset(48)] internal ulong ReservedV1Compat1;
        [FieldOffset(56)] internal uint Win32VirtualKey;
        [FieldOffset(60)] internal uint Win32ScanCode;
        [FieldOffset(64)] internal uint Win32UnicodeCharacter;
        [FieldOffset(68)] internal uint Win32KeyDown;
        [FieldOffset(72)] internal uint Win32ControlKeyState;
        [FieldOffset(76)] internal uint Win32RepeatCount;
        [FieldOffset(80)] internal ulong Reserved0;
        [FieldOffset(88)] internal ulong Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 160)]
    internal struct TlTerminalInputV3
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Kind;
        [FieldOffset(12)] internal uint Key;
        [FieldOffset(16)] internal uint Modifiers;
        [FieldOffset(20)] internal uint Character;
        [FieldOffset(24)] internal IntPtr Data;
        [FieldOffset(32)] internal ulong DataLength;
        [FieldOffset(40)] internal ulong ReservedV1Compat0;
        [FieldOffset(48)] internal ulong ReservedV1Compat1;
        [FieldOffset(56)] internal uint Win32VirtualKey;
        [FieldOffset(60)] internal uint Win32ScanCode;
        [FieldOffset(64)] internal uint Win32UnicodeCharacter;
        [FieldOffset(68)] internal uint Win32KeyDown;
        [FieldOffset(72)] internal uint Win32ControlKeyState;
        [FieldOffset(76)] internal uint Win32RepeatCount;
        [FieldOffset(80)] internal ulong ReservedV2Compat0;
        [FieldOffset(88)] internal ulong ReservedV2Compat1;
        [FieldOffset(96)] internal uint KeyEventType;
        [FieldOffset(100)] internal uint KittyModifiers;
        [FieldOffset(104)] internal uint ShiftedKey;
        [FieldOffset(108)] internal uint BaseLayoutKey;
        [FieldOffset(112)] internal IntPtr AssociatedText;
        [FieldOffset(120)] internal ulong AssociatedTextLength;
        [FieldOffset(128)] internal ulong ReservedV30;
        [FieldOffset(136)] internal ulong ReservedV31;
        [FieldOffset(144)] internal ulong ReservedV32;
        [FieldOffset(152)] internal ulong ReservedV33;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct TlTerminalMouseInputV1
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal uint Action;
        internal uint Button;
        internal uint Wheel;
        internal uint ButtonsHeld;
        internal uint PositionFlags;
        internal uint Modifiers;
        internal uint CellColumn;
        internal uint CellRow;
        internal uint PixelX;
        internal uint PixelY;
        internal ulong Reserved0;
        internal ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TlTerminalBuffer
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal IntPtr Data;
        internal ulong Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TlTerminalCheckpointInfo
    {
        internal uint AbiVersion;
        internal uint StructSize;
        internal ulong Revision;
        internal ulong NextSourceSequence;
        internal ulong Reserved0;
        internal ulong Reserved1;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 48)]
    internal struct TlTerminalPresentationInfoV1
    {
        [FieldOffset(0)] internal uint AbiVersion;
        [FieldOffset(4)] internal uint StructSize;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(12)] internal uint Reserved0;
        [FieldOffset(16)] internal ulong NextSourceSequence;
        [FieldOffset(24)] internal ulong LogicalTimeMicros;
        [FieldOffset(32)] internal ulong SynchronizedOutputDeadlineMicros;
        [FieldOffset(40)] internal ulong Reserved;
    }

    internal static unsafe partial class NativeTerminalInterop
    {
        internal delegate NativeTerminalStatus CheckpointBufferExporter(
            SafeTlTerminalCheckpointHandle checkpoint,
            ref TlTerminalBuffer buffer);

        internal delegate NativeTerminalStatus CheckpointRenderFrameExporter(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer buffer);

        private const string NativeLibrary = "termlink_terminal_ffi.dll";
        private const ulong ExpectedMaxSnapshotBytes = 64UL * 1024UL * 1024UL;
        private const uint ExpectedRenderFrameV1FormatVersion = 1;
        private const ulong ExpectedMaxRenderFrameV1Bytes = 16UL * 1024UL * 1024UL;
        private const uint ExpectedRenderFrameV2FormatVersion = 2;
        private const ulong ExpectedMaxRenderFrameV2Bytes = 16UL * 1024UL * 1024UL;
        private const uint ExpectedRenderFrameV3FormatVersion = 3;
        private const ulong ExpectedMaxRenderFrameV3Bytes = 16UL * 1024UL * 1024UL;
        private const uint ExpectedHostEffectFrameV1FormatVersion = 1;
        private const ulong ExpectedMaxHostEffectFrameV1Bytes = 65_710_862UL;
        private const uint ExpectedHostEffectFrameV2FormatVersion = 2;
        private const ulong ExpectedMaxHostEffectFrameV2Bytes = 123_033_008UL;
        private const uint ExpectedConfigV1StructSize = 40;
        private const uint ExpectedConfigV2StructSize = 64;
        private const uint ExpectedConfigV3StructSize = 88;
        private const uint ExpectedPresentationInfoV1StructSize = 48;
        private const uint OutputEventKind = 1;
        private const uint ResizeEventKind = 2;
        private const uint AdvanceEventKind = 3;
        private const uint GapEventKind = 4;
        private const uint KeyInputKind = 1;
        private const uint PasteInputKind = 2;
        private const uint FocusGainedInputKind = 3;
        private const uint FocusLostInputKind = 4;
        private const uint Win32KeyInputKind = 5;
        private const uint KittyKeyInputKind = 6;
        private const uint MousePositionPixel = 1;
        private const uint ExpectedInputV2StructSize = 96;
        private const uint ExpectedInputV3StructSize = 160;

        internal static readonly uint ConfigV1StructSize = checked((uint)Marshal.SizeOf<TlTerminalConfigV1>());
        internal static readonly uint ConfigV2StructSize = checked((uint)Marshal.SizeOf<TlTerminalConfigV2>());
        internal static readonly uint ConfigV3StructSize = checked((uint)Marshal.SizeOf<TlTerminalConfigV3>());
        internal static readonly uint EventStructSize = checked((uint)Marshal.SizeOf<TlTerminalEvent>());
        internal static readonly uint BufferStructSize = checked((uint)Marshal.SizeOf<TlTerminalBuffer>());
        internal static readonly uint CheckpointInfoStructSize =
            checked((uint)Marshal.SizeOf<TlTerminalCheckpointInfo>());
        internal static readonly uint PresentationInfoV1StructSize =
            checked((uint)Marshal.SizeOf<TlTerminalPresentationInfoV1>());
        internal static readonly uint InputV1StructSize =
            checked((uint)Marshal.SizeOf<TlTerminalInputV1>());
        internal static readonly uint InputV2StructSize =
            checked((uint)Marshal.SizeOf<TlTerminalInputV2>());
        internal static readonly uint InputV3StructSize =
            checked((uint)Marshal.SizeOf<TlTerminalInputV3>());
        internal static readonly uint MouseInputV1StructSize =
            checked((uint)Marshal.SizeOf<TlTerminalMouseInputV1>());

        internal static NativeTerminalAbiInfo ValidateAbi()
        {
            var abiVersion = TerminalAbiVersion();
            var maxSnapshotBytes = TerminalMaxSnapshotBytes();
            var renderFrameV1FormatVersion = TerminalRenderFrameV1FormatVersion();
            var maxRenderFrameV1Bytes = TerminalRenderFrameV1MaxBytes();
            var renderFrameV2FormatVersion = TerminalRenderFrameV2FormatVersion();
            var maxRenderFrameV2Bytes = TerminalRenderFrameV2MaxBytes();
            var renderFrameV3FormatVersion = TerminalRenderFrameV3FormatVersion();
            var maxRenderFrameV3Bytes = TerminalRenderFrameV3MaxBytes();
            var hostEffectFrameV1FormatVersion = TerminalHostEffectFrameV1FormatVersion();
            var maxHostEffectFrameV1Bytes = TerminalHostEffectFrameV1MaxBytes();
            var hostEffectFrameV2FormatVersion = TerminalHostEffectFrameV2FormatVersion();
            var maxHostEffectFrameV2Bytes = TerminalHostEffectFrameV2MaxBytes();
            var configV1Size = TerminalConfigV1StructSize();
            var configV2Size = TerminalConfigV2StructSize();
            var configV3Size = TerminalConfigV3StructSize();
            var eventSize = TerminalEventStructSize();
            var bufferSize = TerminalBufferStructSize();
            var checkpointInfoSize = TerminalCheckpointInfoStructSize();
            var presentationInfoV1Size = TerminalPresentationInfoV1StructSize();
            var inputV1Size = TerminalInputV1StructSize();
            var inputV2Size = TerminalInputV2StructSize();
            var inputV3Size = TerminalInputV3StructSize();
            var mouseInputV1Size = TerminalMouseInputV1StructSize();
            if (abiVersion != NativeTerminalCoreV2.AbiVersion ||
                maxSnapshotBytes != ExpectedMaxSnapshotBytes ||
                renderFrameV1FormatVersion != ExpectedRenderFrameV1FormatVersion ||
                maxRenderFrameV1Bytes != ExpectedMaxRenderFrameV1Bytes ||
                renderFrameV2FormatVersion != ExpectedRenderFrameV2FormatVersion ||
                maxRenderFrameV2Bytes != ExpectedMaxRenderFrameV2Bytes ||
                renderFrameV3FormatVersion != ExpectedRenderFrameV3FormatVersion ||
                maxRenderFrameV3Bytes != ExpectedMaxRenderFrameV3Bytes ||
                hostEffectFrameV1FormatVersion != ExpectedHostEffectFrameV1FormatVersion ||
                maxHostEffectFrameV1Bytes != ExpectedMaxHostEffectFrameV1Bytes ||
                hostEffectFrameV2FormatVersion != ExpectedHostEffectFrameV2FormatVersion ||
                maxHostEffectFrameV2Bytes != ExpectedMaxHostEffectFrameV2Bytes ||
                ConfigV1StructSize != ExpectedConfigV1StructSize ||
                ConfigV2StructSize != ExpectedConfigV2StructSize ||
                ConfigV3StructSize != ExpectedConfigV3StructSize ||
                configV1Size != ExpectedConfigV1StructSize ||
                configV2Size != ExpectedConfigV2StructSize ||
                configV3Size != ExpectedConfigV3StructSize ||
                eventSize != EventStructSize ||
                bufferSize != BufferStructSize ||
                checkpointInfoSize != CheckpointInfoStructSize ||
                PresentationInfoV1StructSize != ExpectedPresentationInfoV1StructSize ||
                presentationInfoV1Size != ExpectedPresentationInfoV1StructSize ||
                inputV1Size != InputV1StructSize ||
                InputV2StructSize != ExpectedInputV2StructSize ||
                inputV2Size != ExpectedInputV2StructSize ||
                InputV3StructSize != ExpectedInputV3StructSize ||
                inputV3Size != ExpectedInputV3StructSize ||
                mouseInputV1Size != MouseInputV1StructSize)
                throw new NativeTerminalAbiException(
                    "Native terminal ABI mismatch. Expected ABI " +
                    NativeTerminalCoreV2.AbiVersion.ToString() + ", max snapshot bytes " +
                    ExpectedMaxSnapshotBytes.ToString() + ", render frame format/bytes " +
                    ExpectedRenderFrameV1FormatVersion.ToString() + "/" +
                    ExpectedMaxRenderFrameV1Bytes.ToString() + ", render frame v2 format/bytes " +
                    ExpectedRenderFrameV2FormatVersion.ToString() + "/" +
                    ExpectedMaxRenderFrameV2Bytes.ToString() +
                    ", render frame v3 format/bytes " +
                    ExpectedRenderFrameV3FormatVersion.ToString() + "/" +
                    ExpectedMaxRenderFrameV3Bytes.ToString() +
                    ", host effect frame v1 format/bytes " +
                    ExpectedHostEffectFrameV1FormatVersion.ToString() + "/" +
                    ExpectedMaxHostEffectFrameV1Bytes.ToString() +
                    ", host effect frame v2 format/bytes " +
                    ExpectedHostEffectFrameV2FormatVersion.ToString() + "/" +
                    ExpectedMaxHostEffectFrameV2Bytes.ToString() + ", and struct sizes " +
                    ExpectedConfigV1StructSize.ToString() + "/" +
                    ExpectedConfigV2StructSize.ToString() + "/" +
                    ExpectedConfigV3StructSize.ToString() + "/" +
                    EventStructSize.ToString() + "/" +
                    BufferStructSize.ToString() + "/" +
                    CheckpointInfoStructSize.ToString() + "/" +
                    ExpectedPresentationInfoV1StructSize.ToString() + "/" +
                    InputV1StructSize.ToString() + "/" +
                    ExpectedInputV2StructSize.ToString() + "/" +
                    ExpectedInputV3StructSize.ToString() + "/" +
                    MouseInputV1StructSize.ToString() + ", received ABI " +
                    abiVersion.ToString() + ", max snapshot bytes " +
                    maxSnapshotBytes.ToString() + ", render frame format/bytes " +
                    renderFrameV1FormatVersion.ToString() + "/" +
                    maxRenderFrameV1Bytes.ToString() + ", render frame v2 format/bytes " +
                    renderFrameV2FormatVersion.ToString() + "/" +
                    maxRenderFrameV2Bytes.ToString() +
                    ", render frame v3 format/bytes " +
                    renderFrameV3FormatVersion.ToString() + "/" +
                    maxRenderFrameV3Bytes.ToString() +
                    ", host effect frame v1 format/bytes " +
                    hostEffectFrameV1FormatVersion.ToString() + "/" +
                    maxHostEffectFrameV1Bytes.ToString() +
                    ", host effect frame v2 format/bytes " +
                    hostEffectFrameV2FormatVersion.ToString() + "/" +
                    maxHostEffectFrameV2Bytes.ToString() + ", and struct sizes " +
                    configV1Size.ToString() + "/" +
                    configV2Size.ToString() + "/" +
                    configV3Size.ToString() + "/" +
                    eventSize.ToString() + "/" +
                    bufferSize.ToString() + "/" +
                    checkpointInfoSize.ToString() + "/" +
                    presentationInfoV1Size.ToString() + "/" +
                    inputV1Size.ToString() + "/" +
                    inputV2Size.ToString() + "/" +
                    inputV3Size.ToString() + "/" +
                    mouseInputV1Size.ToString() + ".");
            return new NativeTerminalAbiInfo(
                abiVersion,
                maxSnapshotBytes,
                renderFrameV1FormatVersion,
                maxRenderFrameV1Bytes,
                renderFrameV2FormatVersion,
                maxRenderFrameV2Bytes,
                renderFrameV3FormatVersion,
                maxRenderFrameV3Bytes,
                hostEffectFrameV1FormatVersion,
                maxHostEffectFrameV1Bytes,
                hostEffectFrameV2FormatVersion,
                maxHostEffectFrameV2Bytes,
                configV1Size,
                configV2Size,
                configV3Size,
                eventSize,
                bufferSize,
                checkpointInfoSize,
                presentationInfoV1Size,
                inputV1Size,
                inputV2Size,
                inputV3Size,
                mouseInputV1Size);
        }

        internal static TlTerminalConfigV1 CreateConfig(
            uint columns,
            uint rows,
            ulong initialSourceSequence)
        {
            return new TlTerminalConfigV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = ConfigV1StructSize,
                Columns = columns,
                Rows = rows,
                InitialSourceSequence = initialSourceSequence
            };
        }

        internal static TlTerminalConfigV2 CreateConfigV2(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows)
        {
            return new TlTerminalConfigV2
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = ConfigV2StructSize,
                Columns = columns,
                Rows = rows,
                InitialSourceSequence = initialSourceSequence,
                HistoryCapacityRows = historyCapacityRows
            };
        }

        internal static void RequireV4Abi()
        {
            if (Marshal.SizeOf<TlTerminalConfigV4>() != 112 || TerminalConfigV4StructSize() != 112 ||
                TerminalRenderFrameV4FormatVersion() != 4 || TerminalRenderFrameV4MaxBytes() != 32UL * 1024 * 1024)
                throw new InvalidOperationException("terminal-core V4 configuration/render ABI mismatch");
        }

        internal static bool SupportsGraphicsV1()
        {
            var library = System.Runtime.InteropServices.NativeLibrary.Load(NativeLibrary,
                typeof(NativeTerminalInterop).Assembly, DllImportSearchPath.AssemblyDirectory);
            try
            {
                foreach (var entryPoint in new[]
                {
                    "tl_terminal_create_graphics_v1", "tl_terminal_import_snapshot_graphics_v1",
                    "tl_terminal_checkpoint_export_snapshot_graphics_v1", "tl_terminal_max_graphics_snapshot_bytes",
                    "tl_terminal_render_frame_v5_format_version", "tl_terminal_render_frame_v5_max_bytes",
                    "tl_terminal_checkpoint_export_render_frame_v5", "tl_terminal_checkpoint_export_render_frame_v5_at_scrollback",
                    "tl_terminal_history_page_v2", "tl_terminal_history_viewport_v2"
                })
                    if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, entryPoint, out _)) return false;
                RequireV4Abi();
                if (TerminalMaxGraphicsSnapshotBytes() != NativeTerminalCoreV2.MaximumGraphicsSnapshotBytes ||
                    TerminalRenderFrameV5FormatVersion() != 5 ||
                    TerminalRenderFrameV5MaxBytes() != NativeTerminalRenderFrameV5Decoder.MaximumFrameBytes)
                    throw new NativeTerminalAbiException("terminal-core graphics snapshot/render ABI mismatch.");
                return true;
            }
            finally { System.Runtime.InteropServices.NativeLibrary.Free(library); }
        }

        internal static void RequireGraphicsV1Abi()
        {
            if (!SupportsGraphicsV1()) throw new NotSupportedException("This terminal-core DLL does not provide the experimental graphics V1 ABI.");
        }

        internal static TlTerminalConfigV3 CreateConfigV3(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows,
            NativeTerminalOutputProfile outputProfile)
        {
            return new TlTerminalConfigV3
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = ConfigV3StructSize,
                Columns = columns,
                Rows = rows,
                InitialSourceSequence = initialSourceSequence,
                HistoryCapacityRows = historyCapacityRows,
                OutputProfile = (uint)outputProfile
            };
        }
        internal static TlTerminalConfigV4 CreateConfigV4(
            uint columns,
            uint rows,
            ulong initialSourceSequence,
            ulong historyCapacityRows,
            NativeTerminalOutputProfile outputProfile, NativeTerminalWidthProfile widthProfile)
        {
            return new TlTerminalConfigV4
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = 112,
                Columns = columns,
                Rows = rows,
                InitialSourceSequence = initialSourceSequence,
                HistoryCapacityRows = historyCapacityRows,
                OutputProfile = (uint)outputProfile, WidthProfile = (uint)widthProfile
            };
        }

        internal static TlTerminalEvent CreateOutputEvent(
            ulong sourceSequence,
            byte* data,
            ulong dataLength)
        {
            return CreateEvent(sourceSequence, OutputEventKind, (IntPtr)data, dataLength, 0, 0, 0);
        }

        internal static TlTerminalEvent CreateResizeEvent(
            ulong sourceSequence,
            uint columns,
            uint rows)
        {
            return CreateEvent(sourceSequence, ResizeEventKind, IntPtr.Zero, 0, columns, rows, 0);
        }

        internal static TlTerminalEvent CreateAdvanceEvent(ulong sourceSequence, ulong micros)
        {
            return CreateEvent(sourceSequence, AdvanceEventKind, IntPtr.Zero, 0, 0, 0, micros);
        }

        internal static TlTerminalEvent CreateGapEvent(ulong sourceSequence)
        {
            return CreateEvent(sourceSequence, GapEventKind, IntPtr.Zero, 0, 0, 0, 0);
        }

        internal static TlTerminalBuffer CreateEmptyBuffer()
        {
            return new TlTerminalBuffer
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = BufferStructSize,
                Data = IntPtr.Zero,
                Length = 0
            };
        }

        internal static TlTerminalCheckpointInfo CreateEmptyCheckpointInfo()
        {
            return new TlTerminalCheckpointInfo
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = CheckpointInfoStructSize
            };
        }

        internal static TlTerminalPresentationInfoV1 CreateEmptyPresentationInfoV1()
        {
            return new TlTerminalPresentationInfoV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = PresentationInfoV1StructSize
            };
        }

        internal static TlTerminalInputV1 CreateKeyInput(
            NativeTerminalKey key,
            NativeTerminalModifiers modifiers,
            uint character)
        {
            return new TlTerminalInputV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = InputV1StructSize,
                Kind = KeyInputKind,
                Key = (uint)key,
                Modifiers = (uint)modifiers,
                Character = character,
                Data = IntPtr.Zero,
                DataLength = 0
            };
        }

        internal static TlTerminalInputV1 CreatePasteInput(byte* data, ulong dataLength)
        {
            return new TlTerminalInputV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = InputV1StructSize,
                Kind = PasteInputKind,
                Key = 0,
                Modifiers = 0,
                Character = 0,
                Data = (IntPtr)data,
                DataLength = dataLength
            };
        }

        internal static TlTerminalInputV1 CreateFocusGainedInput()
        {
            return CreateFocusInput(FocusGainedInputKind);
        }

        internal static TlTerminalInputV1 CreateFocusLostInput()
        {
            return CreateFocusInput(FocusLostInputKind);
        }

        internal static TlTerminalInputV2 CreateWin32KeyInput(
            NativeTerminalWin32KeyEvent keyEvent)
        {
            return new TlTerminalInputV2
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = InputV2StructSize,
                Kind = Win32KeyInputKind,
                Key = 0,
                Modifiers = 0,
                Character = 0,
                Data = IntPtr.Zero,
                DataLength = 0,
                ReservedV1Compat0 = 0,
                ReservedV1Compat1 = 0,
                Win32VirtualKey = keyEvent.VirtualKey,
                Win32ScanCode = keyEvent.ScanCode,
                Win32UnicodeCharacter = keyEvent.UnicodeCharacter,
                Win32KeyDown = keyEvent.KeyDown ? 1U : 0U,
                Win32ControlKeyState = keyEvent.ControlKeyState,
                Win32RepeatCount = keyEvent.RepeatCount,
                Reserved0 = 0,
                Reserved1 = 0
            };
        }

        internal static TlTerminalInputV3 CreateKittyKeyInput(
            NativeTerminalKittyKeyEvent keyEvent,
            IntPtr associatedText,
            ulong associatedTextLength)
        {
            return new TlTerminalInputV3
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = InputV3StructSize,
                Kind = KittyKeyInputKind,
                Key = keyEvent.Identity.KeyValue,
                Modifiers = 0,
                Character = keyEvent.Identity.CharacterScalar,
                Data = IntPtr.Zero,
                DataLength = 0,
                ReservedV1Compat0 = 0,
                ReservedV1Compat1 = 0,
                Win32VirtualKey = 0,
                Win32ScanCode = 0,
                Win32UnicodeCharacter = 0,
                Win32KeyDown = 0,
                Win32ControlKeyState = 0,
                Win32RepeatCount = 0,
                ReservedV2Compat0 = 0,
                ReservedV2Compat1 = 0,
                KeyEventType = (uint)keyEvent.EventType,
                KittyModifiers = (uint)keyEvent.Modifiers,
                ShiftedKey = keyEvent.ShiftedKey.HasValue
                    ? checked((uint)keyEvent.ShiftedKey.Value.Value)
                    : 0,
                BaseLayoutKey = keyEvent.BaseLayoutKey.HasValue
                    ? checked((uint)keyEvent.BaseLayoutKey.Value.Value)
                    : 0,
                AssociatedText = associatedText,
                AssociatedTextLength = associatedTextLength,
                ReservedV30 = 0,
                ReservedV31 = 0,
                ReservedV32 = 0,
                ReservedV33 = 0
            };
        }

        private static TlTerminalInputV1 CreateFocusInput(uint kind)
        {
            return new TlTerminalInputV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = InputV1StructSize,
                Kind = kind,
                Key = 0,
                Modifiers = 0,
                Character = 0,
                Data = IntPtr.Zero,
                DataLength = 0,
                Reserved0 = 0,
                Reserved1 = 0
            };
        }

        internal static TlTerminalMouseInputV1 CreateMouseInput(
            NativeTerminalMouseEvent mouseEvent)
        {
            var position = mouseEvent.Position;
            return new TlTerminalMouseInputV1
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = MouseInputV1StructSize,
                Action = (uint)mouseEvent.Action,
                Button = mouseEvent.ButtonValue,
                Wheel = mouseEvent.WheelValue,
                ButtonsHeld = (uint)mouseEvent.ButtonsHeld,
                PositionFlags = position.HasPixelPosition ? MousePositionPixel : 0,
                Modifiers = (uint)mouseEvent.Modifiers,
                CellColumn = position.CellColumn,
                CellRow = position.CellRow,
                PixelX = position.HasPixelPosition ? position.PixelX : 0,
                PixelY = position.HasPixelPosition ? position.PixelY : 0,
                Reserved0 = 0,
                Reserved1 = 0
            };
        }

        internal static SafeTlTerminalHandle OwnTerminal(IntPtr nativeHandle)
        {
            if (nativeHandle == IntPtr.Zero)
                throw new NativeTerminalAbiException(
                    "The native terminal returned a null terminal handle after success.");
            return new SafeTlTerminalHandle(nativeHandle);
        }

        internal static SafeTlTerminalCheckpointHandle OwnCheckpoint(IntPtr nativeHandle)
        {
            if (nativeHandle == IntPtr.Zero)
                throw new NativeTerminalAbiException(
                    "The native terminal returned a null checkpoint handle after success.");
            return new SafeTlTerminalCheckpointHandle(nativeHandle);
        }

        internal static void RequireEmptyFailedHandle(IntPtr nativeHandle, string operation)
        {
            if (nativeHandle != IntPtr.Zero)
            {
                TerminalCheckpointDestroy(nativeHandle);
                throw new NativeTerminalAbiException(
                    operation + " returned a handle after reporting failure.");
            }
        }

        internal static void RequireEmptyFailedTerminalHandle(
            NativeTerminalStatus status,
            IntPtr nativeHandle,
            string operation)
        {
            if (status != NativeTerminalStatus.Ok && nativeHandle != IntPtr.Zero)
            {
                TerminalDestroy(nativeHandle);
                throw new NativeTerminalAbiException(
                    operation + " returned a terminal handle after reporting failure.");
            }
        }

        internal static void ThrowIfFailed(NativeTerminalStatus status, string operation)
        {
            if (status != NativeTerminalStatus.Ok)
                throw new NativeTerminalException(operation, status);
        }

        internal static bool RequiresTerminalRetirement(NativeTerminalStatus status)
        {
            // Only statuses documented as non-mutating and retryable preserve
            // the wrapper. Unknown statuses conservatively retire the handle.
            switch (status)
            {
                case NativeTerminalStatus.Ok:
                case NativeTerminalStatus.InvalidDimensions:
                case NativeTerminalStatus.LimitExceeded:
                case NativeTerminalStatus.SequenceMismatch:
                case NativeTerminalStatus.TimeOverflow:
                case NativeTerminalStatus.SnapshotNotEligible:
                case NativeTerminalStatus.PresentationDeferred:
                case NativeTerminalStatus.HostEffectVersionRequired:
                case NativeTerminalStatus.RenderFrameVersionRequired:
                    return false;
                default:
                    return true;
            }
        }

        internal static bool RequiresTerminalRetirementAfterInput(NativeTerminalStatus status)
        {
            switch (status)
            {
                case NativeTerminalStatus.Ok:
                case NativeTerminalStatus.InvalidArgument:
                case NativeTerminalStatus.LimitExceeded:
                case NativeTerminalStatus.Unsupported:
                    return false;
                default:
                    return true;
            }
        }

        private static TlTerminalEvent CreateEvent(
            ulong sourceSequence,
            uint kind,
            IntPtr data,
            ulong dataLength,
            uint columns,
            uint rows,
            ulong advanceMicros)
        {
            return new TlTerminalEvent
            {
                AbiVersion = NativeTerminalCoreV2.AbiVersion,
                StructSize = EventStructSize,
                SourceSequence = sourceSequence,
                Kind = kind,
                Reserved = 0,
                Data = data,
                DataLength = dataLength,
                Columns = columns,
                Rows = rows,
                AdvanceMicros = advanceMicros
            };
        }

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_abi_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalAbiVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_max_snapshot_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalMaxSnapshotBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v1_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalRenderFrameV1FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v1_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalRenderFrameV1MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v2_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalRenderFrameV2FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v2_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalRenderFrameV2MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v3_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalRenderFrameV3FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v4_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalRenderFrameV4FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v3_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalRenderFrameV3MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v4_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalRenderFrameV4MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_host_effect_frame_v1_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalHostEffectFrameV1FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_host_effect_frame_v1_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalHostEffectFrameV1MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_host_effect_frame_v2_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalHostEffectFrameV2FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_host_effect_frame_v2_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalHostEffectFrameV2MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_config_v1_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalConfigV1StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_config_v2_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalConfigV2StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_config_v3_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalConfigV3StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_config_v4_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalConfigV4StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_event_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalEventStructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_buffer_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalBufferStructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_info_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalCheckpointInfoStructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_presentation_info_v1_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalPresentationInfoV1StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_input_v1_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalInputV1StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_input_v2_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalInputV2StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_input_v3_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalInputV3StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_mouse_input_v1_struct_size",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalMouseInputV1StructSize();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_create",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCreate(
            in TlTerminalConfigV1 config,
            ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_create_v2",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCreateV2(
            in TlTerminalConfigV2 config,
            ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_create_v3",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCreateV3(
            in TlTerminalConfigV3 config,
            ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_create_v4",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCreateV4(
            in TlTerminalConfigV4 config,
            ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_destroy",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TerminalDestroy(IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_apply_event",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEvent(
            SafeTlTerminalHandle terminal,
            in TlTerminalEvent terminalEvent);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_apply_event_batch_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEventBatchV1(
            SafeTlTerminalHandle terminal,
            TlTerminalEvent* events,
            uint eventCount);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_apply_event_with_host_effects_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEventWithHostEffectsV1(
            SafeTlTerminalHandle terminal,
            in TlTerminalEvent terminalEvent,
            ref TlTerminalBuffer effectFrame);

        [DllImport(
            NativeLibrary,
            EntryPoint = "tl_terminal_apply_event_batch_with_host_effects_v1",
            ExactSpelling = true,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEventBatchWithHostEffectsV1(
            SafeTlTerminalHandle terminal,
            TlTerminalEvent* events,
            uint eventCount,
            ref TlTerminalBuffer effectFrame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_apply_event_with_host_effects_v2",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEventWithHostEffectsV2(
            SafeTlTerminalHandle terminal,
            in TlTerminalEvent terminalEvent,
            ref TlTerminalBuffer effectFrame);

        [DllImport(
            NativeLibrary,
            EntryPoint = "tl_terminal_apply_event_batch_with_host_effects_v2",
            ExactSpelling = true,
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalApplyEventBatchWithHostEffectsV2(
            SafeTlTerminalHandle terminal,
            TlTerminalEvent* events,
            uint eventCount,
            ref TlTerminalBuffer effectFrame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_get_presentation_info_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalGetPresentationInfoV1(
            SafeTlTerminalHandle terminal,
            ref TlTerminalPresentationInfoV1 info);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_import_snapshot",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalImportSnapshot(
            byte* snapshot,
            ulong snapshotLength,
            ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_begin",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointBegin(
            SafeTlTerminalHandle terminal,
            ref IntPtr checkpoint);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_destroy",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TerminalCheckpointDestroy(IntPtr checkpoint);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_get_info",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointGetInfo(
            SafeTlTerminalCheckpointHandle checkpoint,
            ref TlTerminalCheckpointInfo info);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_snapshot",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportSnapshot(
            SafeTlTerminalCheckpointHandle checkpoint,
            ref TlTerminalBuffer snapshot);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV1(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v2",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV2(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v3",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV3(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_shell_state_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportShellStateV1(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v4",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV4(
            SafeTlTerminalCheckpointHandle checkpoint,
            ulong maxFrameBytes,
            ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v4_at_scrollback",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV4AtScrollback(
            SafeTlTerminalCheckpointHandle checkpoint, uint offsetRows, ulong maxFrameBytes, ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_delta",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportDelta(
            SafeTlTerminalCheckpointHandle checkpoint,
            ref TlTerminalBuffer delta);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV1(
            SafeTlTerminalHandle terminal,
            in TlTerminalInputV1 input,
            ref TlTerminalBuffer encoded);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v1_with_diagnostic",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV1WithDiagnostic(
            SafeTlTerminalHandle terminal, in TlTerminalInputV1 input,
            ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v2",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV2(
            SafeTlTerminalHandle terminal,
            in TlTerminalInputV2 input,
            ref TlTerminalBuffer encoded);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v2_with_diagnostic",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV2WithDiagnostic(
            SafeTlTerminalHandle terminal, in TlTerminalInputV2 input,
            ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v3",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV3(
            SafeTlTerminalHandle terminal,
            in TlTerminalInputV3 input,
            ref TlTerminalBuffer encoded);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_input_v3_with_diagnostic",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeInputV3WithDiagnostic(
            SafeTlTerminalHandle terminal, in TlTerminalInputV3 input,
            ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_mouse_input_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeMouseInputV1(
            SafeTlTerminalHandle terminal,
            in TlTerminalMouseInputV1 input,
            ref TlTerminalBuffer encoded);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_encode_mouse_input_v1_with_diagnostic",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalEncodeMouseInputV1WithDiagnostic(
            SafeTlTerminalHandle terminal, in TlTerminalMouseInputV1 input,
            ref TlTerminalBuffer encoded, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_export_failure_diagnostic_v1",
            CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern NativeTerminalStatus TerminalExportFailureDiagnosticV1(
            SafeTlTerminalHandle terminal, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_export_failure_diagnostic_v2",
            CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern NativeTerminalStatus TerminalExportFailureDiagnosticV2(
            SafeTlTerminalHandle terminal, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_export_failure_diagnostic_v3",
            CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern NativeTerminalStatus TerminalExportFailureDiagnosticV3(
            SafeTlTerminalHandle terminal, ref TlTerminalBuffer diagnostic);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_create_graphics_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCreateGraphicsV1(in TlTerminalConfigV4 config, ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_import_snapshot_graphics_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalImportSnapshotGraphicsV1(byte* bytes, ulong length, ref IntPtr terminal);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_snapshot_graphics_v1",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportSnapshotGraphicsV1(
            SafeTlTerminalCheckpointHandle checkpoint, ref TlTerminalBuffer snapshot);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_max_graphics_snapshot_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalMaxGraphicsSnapshotBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v5_format_version",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint TerminalRenderFrameV5FormatVersion();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_render_frame_v5_max_bytes",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong TerminalRenderFrameV5MaxBytes();

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v5",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV5(
            SafeTlTerminalCheckpointHandle checkpoint, uint flags, ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_checkpoint_export_render_frame_v5_at_scrollback",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalCheckpointExportRenderFrameV5AtScrollback(
            SafeTlTerminalCheckpointHandle checkpoint, uint offsetRows, uint flags, ref TlTerminalBuffer frame);

        [DllImport(NativeLibrary, EntryPoint = "tl_terminal_buffer_free",
            ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        internal static extern NativeTerminalStatus TerminalBufferFree(ref TlTerminalBuffer buffer);
    }
}
