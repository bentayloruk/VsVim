﻿using System;
using System.Linq;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Moq;
using NUnit.Framework;
using Vim;
using Vim.Modes;
using Vim.Modes.Visual;
using Vim.UnitTest;
using Vim.UnitTest.Mock;
using GlobalSettings = Vim.GlobalSettings;

namespace VimCore.UnitTest
{
    [TestFixture]
    public class VisualModeTest
    {
        private MockRepository _factory;
        private IWpfTextView _textView;
        private ITextBuffer _textBuffer;
        private ITextSelection _selection;
        private Mock<IVimHost> _host;
        private Mock<IVimBuffer> _bufferData;
        private VisualMode _modeRaw;
        private IMode _mode;
        private IRegisterMap _map;
        private IMarkMap _markMap;
        private Mock<IIncrementalSearch> _incrementalSearch;
        private Mock<ICommonOperations> _operations;
        private Mock<ISelectionTracker> _tracker;
        private Mock<IFoldManager> _foldManager;
        private Mock<IUndoRedoOperations> _undoRedoOperations;
        private Mock<IEditorOperations> _editorOperations;
        private Mock<IJumpList> _jumpList;
        private Mock<ICommandUtil> _commandUtil;

        public void Create(params string[] lines)
        {
            Create2(lines: lines);
        }

        public void Create2(
            ModeKind kind = ModeKind.VisualCharacter,
            params string[] lines)
        {
            _textView = EditorUtil.CreateView(lines);
            _textBuffer = _textView.TextBuffer;
            _selection = _textView.Selection;
            _factory = new MockRepository(MockBehavior.Strict);
            _map = VimUtil.CreateRegisterMap(MockObjectFactory.CreateClipboardDevice(_factory).Object);
            _markMap = new MarkMap(new TrackingLineColumnService());
            _tracker = _factory.Create<ISelectionTracker>();
            _tracker.Setup(x => x.Start());
            _tracker.Setup(x => x.ResetCaret());
            _tracker.Setup(x => x.UpdateSelection());
            _jumpList = _factory.Create<IJumpList>(MockBehavior.Loose);
            _undoRedoOperations = _factory.Create<IUndoRedoOperations>();
            _foldManager = _factory.Create<IFoldManager>();
            _editorOperations = _factory.Create<IEditorOperations>();
            _operations = _factory.Create<ICommonOperations>();
            _operations.SetupGet(x => x.FoldManager).Returns(_foldManager.Object);
            _operations.SetupGet(x => x.UndoRedoOperations).Returns(_undoRedoOperations.Object);
            _operations.SetupGet(x => x.EditorOperations).Returns(_editorOperations.Object);
            _operations.SetupGet(x => x.TextView).Returns(_textView);
            _host = _factory.Create<IVimHost>(MockBehavior.Loose);
            _commandUtil = _factory.Create<ICommandUtil>();
            _commandUtil
                .Setup(x => x.RunCommand(It.Is<Command>(y => y.IsLegacyCommand)))
                .Returns<Command>(c => c.AsLegacyCommand().Item.Function.Invoke(null));
            _incrementalSearch = MockObjectFactory.CreateIncrementalSearch(factory: _factory);
            var globalSettings = new GlobalSettings();
            var localSettings = new LocalSettings(globalSettings, _textView);
            var motionUtil = VimUtil.CreateTextViewMotionUtil(
                _textView,
                _markMap,
                localSettings);
            _bufferData = MockObjectFactory.CreateVimBuffer(
                _textView,
                "test",
                MockObjectFactory.CreateVim(_map, host: _host.Object, settings: globalSettings).Object,
                incrementalSearch: _incrementalSearch.Object,
                jumpList: _jumpList.Object,
                motionUtil: motionUtil);
            var capture = new MotionCapture(
                _host.Object,
                _textView,
                _incrementalSearch.Object,
                localSettings);
            var runner = new CommandRunner(
                _textView,
                _map,
                capture,
                _commandUtil.Object,
                (new Mock<IStatusUtil>()).Object,
                VisualKind.Character);
            _modeRaw = new VisualMode(_bufferData.Object, _operations.Object, kind, runner, capture, _tracker.Object);
            _mode = _modeRaw;
            _mode.OnEnter(ModeArgument.None);
        }

        [Test, Description("Movement commands")]
        public void Commands1()
        {
            Create("foo");
            var list = new KeyInput[] {
                KeyInputUtil.CharToKeyInput('h'),
                KeyInputUtil.CharToKeyInput('j'),
                KeyInputUtil.CharToKeyInput('k'),
                KeyInputUtil.CharToKeyInput('l'),
                KeyInputUtil.VimKeyToKeyInput(VimKey.Left),
                KeyInputUtil.VimKeyToKeyInput(VimKey.Right),
                KeyInputUtil.VimKeyToKeyInput(VimKey.Up),
                KeyInputUtil.VimKeyToKeyInput(VimKey.Down),
                KeyInputUtil.VimKeyToKeyInput(VimKey.Back) };
            var commands = _mode.CommandNames.ToList();
            foreach (var item in list)
            {
                var name = KeyInputSet.NewOneKeyInput(item);
                Assert.Contains(name, commands);
            }
        }

        [Test]
        public void Process1()
        {
            Create("foo");
            var res = _mode.Process(KeyInputUtil.EscapeKey);
            Assert.IsTrue(res.IsSwitchPreviousMode());
        }

        [Test, Description("Escape should always escape even if we're processing an inner key sequence")]
        public void Process2()
        {
            Create("foo");
            _mode.Process('g');
            var res = _mode.Process(KeyInputUtil.EscapeKey);
            Assert.IsTrue(res.IsSwitchPreviousMode());
        }

        [Test]
        public void OnLeave1()
        {
            _tracker.Setup(x => x.Stop()).Verifiable();
            _mode.OnLeave();
            _tracker.Verify();
        }

        [Test, Description("Must handle arbitrary input to prevent changes but don't list it as a command")]
        public void PreventInput1()
        {
            Create(lines: "foo");
            var input = KeyInputUtil.CharToKeyInput('@');
            _operations.Setup(x => x.Beep()).Verifiable();
            Assert.IsFalse(_mode.CommandNames.Any(x => x.KeyInputs.First().Char == input.Char));
            Assert.IsTrue(_mode.CanProcess(input));
            var ret = _mode.Process(input);
            Assert.IsTrue(ret.IsHandledNoSwitch());
            _operations.Verify();
        }

        [Test]
        [Description("Clear the selection when leaving Visual Mode")]
        public void ChangeModeToNormalShouldClearSelection()
        {
            Create(lines: "foo");
            _selection.Select(_textView.GetLine(0).Extent);
            _mode.Process(KeyInputUtil.EscapeKey);
            Assert.IsTrue(_selection.GetSpan().IsEmpty);
        }

        [Test]
        [Description("Selection should be visible for the command mode operation")]
        public void ChangeModeToCommandShouldNotClearSelection()
        {
            Create(lines: "foo");
            _selection.Select(_textView.GetLine(0).Extent);
            _mode.Process(':');
            Assert.IsFalse(_selection.GetSpan().IsEmpty);
        }

        [Test]
        public void Yank1()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetLineRange(0).Extent;
            _selection.Select(span);
            Assert.IsTrue(_mode.Process('y').IsSwitchPreviousMode());
            Assert.AreEqual("foo", _map.GetRegister(RegisterName.Unnamed).StringValue);
        }

        [Test, Description("Yank should go back to normal mode")]
        public void Yank2()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetLineRange(0).Extent;
            _selection.Select(span);
            var res = _mode.Process('y');
            Assert.IsTrue(res.IsSwitchPreviousMode());
            Assert.AreEqual("foo", _map.GetRegister(RegisterName.Unnamed).StringValue);
        }

        [Test]
        public void Yank3()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetLineRange(0).Extent;
            _selection.Select(span);
            _mode.Process("\"cy");
            Assert.AreEqual("foo", _map.GetRegister('c').StringValue);
        }

        [Test]
        [Description("Yank should reset the caret")]
        public void Yank4()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetLineRange(0).Extent;
            _tracker.Setup(x => x.ResetCaret()).Verifiable();
            _selection.Select(span);
            Assert.IsTrue(_mode.Process('y').IsSwitchPreviousMode());
            Assert.AreEqual("foo", _map.GetRegister(RegisterName.Unnamed).StringValue);
            _tracker.Verify();
        }


        [Test]
        public void Yank_Y_1()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            Assert.IsTrue(_mode.Process('Y').IsSwitchPreviousMode());
            Assert.AreEqual(_textBuffer.GetLineRange(0).GetTextIncludingLineBreak(), _map.GetRegister(RegisterName.Unnamed).StringValue);
        }

        [Test]
        public void Yank_Y_2()
        {
            Create2(ModeKind.VisualLine, "foo", "bar");
            var span = _textBuffer.GetLineRange(0).ExtentIncludingLineBreak;
            _selection.Select(span);
            _mode.Process('y');
            Assert.AreEqual("foo" + Environment.NewLine, _map.GetRegister(RegisterName.Unnamed).StringValue);
            Assert.AreEqual(OperationKind.LineWise, _map.GetRegister(RegisterName.Unnamed).RegisterValue.OperationKind);
        }

        [Test]
        public void Yank_Y_3()
        {
            Create("foo", "bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            Assert.IsTrue(_mode.Process('Y').IsSwitchPreviousMode());
            Assert.AreEqual(_textBuffer.GetLineRange(0).GetTextIncludingLineBreak(), _map.GetRegister(RegisterName.Unnamed).StringValue);
            Assert.AreEqual(OperationKind.LineWise, _map.GetRegister(RegisterName.Unnamed).RegisterValue.OperationKind);
        }

        [Test]
        public void Bind_DeleteSelectedText()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.DeleteSelection);
            _mode.Process("d");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_DeleteSelectedText_ViaDelete()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.DeleteSelection);
            _mode.Process(VimKey.Delete);
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_DeleteSelectedText_ViaX()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.DeleteSelection);
            _mode.Process("x");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_Join_RemoveEmptySpaces()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewJoinSelection(JoinKind.RemoveEmptySpaces));
            _mode.Process("J");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_Join_KeepEmptySpaces()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewJoinSelection(JoinKind.KeepEmptySpaces));
            _mode.Process("gJ");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeSelection()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.ChangeSelection);
            _mode.Process('c');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeSelection_ViaS()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.ChangeSelection);
            _mode.Process('s');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeLineSelection()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewChangeLineSelection(true));
            _mode.Process('C');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeLineSelection_ViaS()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewChangeLineSelection(false));
            _mode.Process('S');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeLineSelection_ViaR()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewChangeLineSelection(false));
            _mode.Process('R');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ChangeCase_Tilde()
        {
            Create("foo bar", "baz");
            _commandUtil.SetupCommandVisual(VisualCommand.NewChangeCase(ChangeCharacterKind.ToggleCase));
            _mode.Process('~');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ShiftLeft()
        {
            Create("foo bar baz");
            _commandUtil.SetupCommandVisual(VisualCommand.ShiftLinesLeft);
            _mode.Process('<');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ShiftRight()
        {
            Create("foo bar baz");
            _commandUtil.SetupCommandVisual(VisualCommand.ShiftLinesRight);
            _mode.Process('>');
            _operations.Verify();
        }

        [Test]
        public void Bind_DeleteLineSelection()
        {
            Create("cat", "tree", "dog");
            _commandUtil.SetupCommandVisual(VisualCommand.DeleteLineSelection);
            _mode.Process("D");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_PutOverSelection()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewPutOverSelection(false));
            _mode.Process('p');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_PutOverCaret_WithCaretMove()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewPutOverSelection(true));
            _mode.Process("gp");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_PutOverSelectio_ViaP()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewPutOverSelection(false));
            _mode.Process('P');
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_PutPutOverSelection_WithCaretMoveViaP()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.NewPutOverSelection(true));
            _mode.Process("gP");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_ReplaceSelection()
        {
            Create("");
            var keyInput = KeyInputUtil.CharToKeyInput('c');
            _commandUtil.SetupCommandVisual(VisualCommand.NewReplaceSelection(keyInput));
            _mode.Process("rc");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_DeleteLineSelection_ViaX()
        {
            Create("cat", "tree", "dog");
            _commandUtil.SetupCommandVisual(VisualCommand.DeleteLineSelection);
            _mode.Process("X");
            _commandUtil.Verify();
        }

        [Test]
        public void Fold_zo()
        {
            Create("foo bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            _operations.Setup(x => x.OpenFold(span, 1)).Verifiable();
            _mode.Process("zo");
            _factory.Verify();
        }

        [Test]
        public void Fold_zc_1()
        {
            Create("foo bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            _operations.Setup(x => x.CloseFold(span, 1)).Verifiable();
            _mode.Process("zc");
            _factory.Verify();
        }

        [Test]
        public void Fold_zO()
        {
            Create("foo bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            _operations.Setup(x => x.OpenAllFolds(span)).Verifiable();
            _mode.Process("zO");
            _factory.Verify();
        }

        [Test]
        public void Fold_zC()
        {
            Create("foo bar");
            var span = _textBuffer.GetSpan(0, 1);
            _selection.Select(span);
            _operations.Setup(x => x.CloseAllFolds(span)).Verifiable();
            _mode.Process("zC");
            _factory.Verify();
        }

        [Test]
        public void Bind_FoldSelection()
        {
            Create("foo bar");
            _commandUtil.SetupCommandVisual(VisualCommand.FoldSelection);
            _mode.Process("zf");
            _commandUtil.Verify();
        }

        [Test]
        public void Fold_zd()
        {
            Create("foo bar");
            _operations.Setup(x => x.DeleteOneFoldAtCursor()).Verifiable();
            _mode.Process("zd");
            _factory.Verify();
        }

        [Test]
        public void Fold_zD()
        {
            Create("foo bar");
            _operations.Setup(x => x.DeleteAllFoldsAtCursor()).Verifiable();
            _mode.Process("zD");
            _factory.Verify();
        }

        [Test]
        public void Fold_zE()
        {
            Create("foo bar");
            _foldManager.Setup(x => x.DeleteAllFolds()).Verifiable();
            _mode.Process("zE");
            _factory.Verify();
        }

        [Test]
        public void SwitchMode1()
        {
            Create("foo bar");
            var ret = _mode.Process(":");
            Assert.IsTrue(ret.IsSwitchModeWithArgument(ModeKind.Command, ModeArgument.FromVisual));
        }

        [Test]
        public void PageUp1()
        {
            Create("");
            _editorOperations.Setup(x => x.PageUp(false)).Verifiable();
            _tracker.Setup(x => x.UpdateSelection()).Verifiable();
            _mode.Process(KeyNotationUtil.StringToKeyInput("<PageUp>"));
            _factory.Verify();
        }

        [Test]
        public void PageDown1()
        {
            Create("");
            _editorOperations.Setup(x => x.PageDown(false)).Verifiable();
            _tracker.Setup(x => x.UpdateSelection()).Verifiable();
            _mode.Process(KeyNotationUtil.StringToKeyInput("<PageDown>"));
            _factory.Verify();
        }

        [Test]
        public void Bind_FormatLines()
        {
            Create("");
            _commandUtil.SetupCommandVisual(VisualCommand.FormatLines);
            _mode.Process("=");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_Motion_LastSearch()
        {
            Create("");
            _commandUtil.SetupCommandNormal(NormalCommand.NewMoveCaretToMotion(Motion.NewLastSearch(true)));
            _mode.Process("N");
            _commandUtil.Verify();
        }

        [Test]
        public void Bind_Motion_LastSearchReverse()
        {
            Create("");
            _commandUtil.SetupCommandNormal(NormalCommand.NewMoveCaretToMotion(Motion.NewLastSearch(false)));
            _mode.Process("n");
            _commandUtil.Verify();
        }
    }
}
