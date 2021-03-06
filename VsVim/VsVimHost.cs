﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Platform.WindowManagement;
using Microsoft.VisualStudio.PlatformUI.Shell;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Vim;
using Vim.Extensions;
using Vim.UI.Wpf;
using VsVim.Properties;

namespace VsVim
{
    /// <summary>
    /// Implement the IVimHost interface for Visual Studio functionality.  It's not responsible for 
    /// the relationship with the IVimBuffer, merely implementing the host functionality
    /// </summary>
    [Export(typeof(IVimHost))]
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(Vim.Constants.ContentType)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class VsVimHost : VimHost
    {
        internal const string CommandNameGoToDefinition = "Edit.GoToDefinition";

        private readonly IVsAdapter _adapter;
        private readonly ITextManager _textManager;
        private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
        private readonly _DTE _dte;

        internal _DTE DTE
        {
            get { return _dte; }
        }

        [ImportingConstructor]
        internal VsVimHost(
            IVsAdapter adapter,
            ITextBufferUndoManagerProvider undoManagerProvider,
            IVsEditorAdaptersFactoryService editorAdaptersFactoryService,
            ITextManager textManager,
            ITextDocumentFactoryService textDocumentFactoryService,
            IEditorOperationsFactoryService editorOperationsFactoryService,
            SVsServiceProvider serviceProvider)
            : base(textDocumentFactoryService, editorOperationsFactoryService)
        {
            _adapter = adapter;
            _editorAdaptersFactoryService = editorAdaptersFactoryService;
            _dte = (_DTE)serviceProvider.GetService(typeof(_DTE));
            _textManager = textManager;
        }

        private bool SafeExecuteCommand(string command, string args = "")
        {
            try
            {
                _dte.ExecuteCommand(command, args);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The C++ project system requires that the target of GoToDefinition be passed
        /// as an argument to the command.  
        /// </summary>
        private bool GoToDefinitionCPlusPlus(ITextView textView, string target)
        {
            if (target == null)
            {
                var caretPoint = textView.Caret.Position.BufferPosition;
                var span = TssUtil.FindCurrentFullWordSpan(caretPoint, WordKind.NormalWord);
                target = span.IsSome()
                    ? span.Value.GetText()
                    : null;
            }

            if (target != null)
            {
                return SafeExecuteCommand(CommandNameGoToDefinition, target);
            }

            return SafeExecuteCommand(CommandNameGoToDefinition);
        }

        private bool GoToDefinitionCore(ITextView textView, string target)
        {
            if (textView.TextBuffer.ContentType.IsCPlusPlus())
            {
                return GoToDefinitionCPlusPlus(textView, target);
            }

            return SafeExecuteCommand(CommandNameGoToDefinition);
        }

        private List<View> GetActiveViews()
        {
            var activeView = ViewManager.Instance.ActiveView;
            if (activeView == null)
            {
                return new List<View>();
            }

            var group = activeView.Parent as DocumentGroup;
            if (group == null)
            {
                return new List<View>();
            }

            return group.VisibleChildren.OfType<View>().ToList();
        }

        /// <summary>
        /// Format the specified line range.  There is no inherent operation to do this
        /// in Visual Studio.  Instead we leverage the FormatSelection command.  Need to be careful
        /// to reset the selection after a format
        /// </summary>
        public override void FormatLines(ITextView textView, SnapshotLineRange range)
        {
            var startedWithSelection = !textView.Selection.IsEmpty;
            textView.Selection.Clear();
            textView.Selection.Select(range.ExtentIncludingLineBreak, false);
            SafeExecuteCommand("Edit.FormatSelection");
            if (!startedWithSelection)
            {
                textView.Selection.Clear();
            }
        }

        public override bool GoToDefinition()
        {
            return GoToDefinitionCore(_textManager.ActiveTextView, null);
        }

        /// <summary>
        /// In a perfect world this would replace the contents of the existing ITextView
        /// with those of the specified file.  Unfortunately this causes problems in 
        /// Visual Studio when the file is of a different content type.  Instead we 
        /// mimic the behavior by opening the document in a new window and closing the
        /// existing one
        /// </summary>
        public override HostResult LoadFileIntoExistingWindow(string filePath, ITextBuffer textBuffer)
        {
            try
            {
                // Open the document before closing the other.  That way any error which occurs
                // during an open will cause the method to abandon and produce a user error 
                // message
                VsShellUtilities.OpenDocument(_adapter.ServiceProvider, filePath);
                _textManager.Close(textBuffer, false);
                return HostResult.Success;
            }
            catch (Exception e)
            {
                return HostResult.NewError(e.Message);
            }
        }

        /// <summary>
        /// Open up a new document window with the specified file
        /// </summary>
        public override HostResult LoadFileIntoNewWindow(string filePath)
        {
            try
            {
                VsShellUtilities.OpenDocument(_adapter.ServiceProvider, filePath);
                return HostResult.Success;
            }
            catch (Exception e)
            {
                return HostResult.NewError(e.Message);
            }
        }

        public override void ShowOpenFileDialog()
        {
            SafeExecuteCommand("Edit.OpenFile");
        }

        public override bool NavigateTo(VirtualSnapshotPoint point)
        {
            return _textManager.NavigateTo(point);
        }

        public override string GetName(ITextBuffer buffer)
        {
            var vsTextLines = _editorAdaptersFactoryService.GetBufferAdapter(buffer) as IVsTextLines;
            if (vsTextLines == null)
            {
                return String.Empty;
            }
            return vsTextLines.GetFileName();
        }

        public override bool Save(ITextBuffer textBuffer)
        {
            return _textManager.Save(textBuffer).IsSuccess;
        }

        public override bool SaveTextAs(string text, string fileName)
        {
            try
            {
                File.WriteAllText(fileName, text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override bool SaveAllFiles()
        {
            var anyFailed = false;
            var all = _textManager.TextBuffers;
            foreach (var textBuffer in all)
            {
                if (_textManager.Save(textBuffer).IsError)
                {
                    anyFailed = true;
                }
            }

            return anyFailed;
        }

        public override void CloseAllFiles(bool checkDirty)
        {
            var all = _textManager.TextViews
                .Select(x => x.TextBuffer)
                .Distinct()
                .ToList();
            foreach (var textBuffer in all)
            {
                _textManager.Close(textBuffer, checkDirty);
            }
        }

        public override void Close(ITextView textView, bool checkDirty)
        {
            _textManager.CloseView(textView, checkDirty);
        }

        public override void GoToNextTab(Vim.Path direction, int count)
        {
            var children = GetActiveViews();
            var activeView = ViewManager.Instance.ActiveView;
            var index = children.IndexOf(activeView);
            if (index == -1)
            {
                return;
            }

            if (direction.IsForward)
            {
                index += count;
            }
            else
            {
                index -= count;
            }

            index %= children.Count;
            children[index].ShowInFront();
        }

        public override void GoToTab(int index)
        {
            View targetView;
            var children = GetActiveViews();
            if (index < 0)
            {
                targetView = children[children.Count - 1];
            }
            else if (index == 0)
            {
                targetView = children[0];
            }
            else
            {
                index -= 1;
                targetView = index < children.Count ? children[index] : null;
            }

            if (targetView == null)
            {
                return;
            }

            targetView.ShowInFront();
        }

        public override void BuildSolution()
        {
            SafeExecuteCommand("Build.BuildSolution");
        }

        /// <summary>
        /// Returns the ITextView which should have keyboard focus.  This method is used during macro
        /// running and hence must account for view changes which occur during a macro run.  Say by the
        /// macro containing the 'gt' command.  Unfortunately these don't fully process through Visual
        /// Studio until the next UI thread pump so we instead have to go straight to the view controller
        /// </summary>
        public override FSharpOption<ITextView> GetFocusedTextView()
        {
            var activeView = ViewManager.Instance.ActiveView;

            // Now find the ITextBuffer which has the matching View instance
            foreach (var textBuffer in _textManager.TextBuffers)
            {
                var result = _adapter.GetContainingWindowFrame(textBuffer);
                if (result.IsError)
                {
                    continue;
                }

                var frame = result.Value as WindowFrame;
                if (frame != null && frame.FrameView == activeView)
                {
                    // TODO: Should try and pick the ITextView which is actually focussed as 
                    // there could be several in a split screen
                    var textView = _textManager.GetTextViews(textBuffer).FirstOrDefault();
                    if (textView != null)
                    {
                        return FSharpOption.Create(textView);
                    }
                }
            }

            return FSharpOption<ITextView>.None;
        }

        /// <summary>
        /// Perform a horizontal window split 
        /// </summary>
        public override HostResult SplitViewHorizontally(ITextView textView)
        {
            _textManager.SplitView(textView);
            return HostResult.Success;
        }

        /// <summary>
        /// Verticaly window splits are not supported in visual studio
        /// </summary>
        public override HostResult SplitViewVertically(ITextView value)
        {
            return HostResult.NewError(Resources.UnsupportedInVisualStudio);
        }

        public override void MoveViewDown(ITextView textView)
        {
            _textManager.MoveViewDown(textView);
        }

        public override void MoveViewUp(ITextView textView)
        {
            _textManager.MoveViewUp(textView);
        }

        /// <summary>
        /// Vertical splits are not supported in Visual Studio so this method is not applicable
        /// </summary>
        public override void MoveViewLeft(ITextView value)
        {
            // Unsupported
        }

        /// <summary>
        /// Vertical splits are not supported in Visual Studio so this method is not applicable
        /// </summary>
        public override void MoveViewRight(ITextView value)
        {
            // Unsupported
        }

        public override bool GoToGlobalDeclaration(ITextView textView, string target)
        {
            return GoToDefinitionCore(textView, target);
        }

        public override bool GoToLocalDeclaration(ITextView textView, string target)
        {
            // This is technically incorrect as it should prefer local declarations. However 
            // there is currently no better way in Visual Studio.  Added this method though
            // so it's easier to plug in later should such an API become available
            return GoToDefinitionCore(textView, target);
        }
    }
}
