﻿#light

namespace Vim.Modes
open Vim
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Outlining
open System.Text.RegularExpressions

type internal CommonOperations ( _data : OperationsData ) =
    let _textBuffer = _data.TextView.TextBuffer
    let _textView = _data.TextView
    let _operations = _data.EditorOperations
    let _outlining = _data.OutliningManager
    let _vimData = _data.VimData
    let _host = _data.VimHost
    let _jumpList = _data.JumpList
    let _settings = _data.LocalSettings
    let _options = _data.EditorOptions
    let _undoRedoOperations = _data.UndoRedoOperations
    let _statusUtil = _data.StatusUtil
    let _wordNavigator =  _data.Navigator
    let _registerMap = _data.RegisterMap
    let _search = _data.SearchService
    let _regexFactory = VimRegexFactory(_data.LocalSettings.GlobalSettings)
    let _globalSettings = _settings.GlobalSettings

    member x.CurrentSnapshot = _textBuffer.CurrentSnapshot

    /// Whether or not to use spaces over tabs where applicable
    member x.UseSpaces = 
        if _settings.GlobalSettings.UseEditorTabSettings then 
            DefaultOptionExtensions.IsConvertTabsToSpacesEnabled(_options) 
        else
            _settings.ExpandTab

    /// How many spaces does a tab count for 
    member x.TabSize =
        if _settings.GlobalSettings.UseEditorTabSettings then
            DefaultOptionExtensions.GetTabSize(_options)
        else
            _settings.TabStop

    /// The caret sometimes needs to be adjusted after an Up or Down movement.  Caret position
    /// and virtual space is actually quite a predicamite for VsVim because of how Vim standard 
    /// works.  Vim has no concept of Virtual Space and is designed to work in a fixed width
    /// font buffer.  Visual Studio has essentially the exact opposite.  Non-fixed width fonts are
    /// the most problematic because it makes the natural Vim motion of column based up and down
    /// make little sense visually.  Instead we rely on the core editor for up and down motions.
    ///
    /// The one exception has to do with the VirtualEdit setting.  By default the 'l' motion will 
    /// only move you to the last character on the line and no further.  Visual Studio up and down
    /// though acts like virtualedit=onemore.  We correct this here
    member x.MoveCaretForVirtualEdit () =
        if not _settings.GlobalSettings.IsVirtualEditOneMore then 
            let point = TextViewUtil.GetCaretPoint _textView
            let line = SnapshotPointUtil.GetContainingLine point
            if point.Position >= line.End.Position && line.Length > 0 then 
                TextViewUtil.MoveCaretToPoint _textView (line.End.Subtract(1))

    /// Move the caret to the specified point and ensure it's visible and the surrounding 
    /// text is expanded
    member x.MoveCaretToPointAndEnsureVisible point = 
        TextViewUtil.MoveCaretToPoint _textView point
        x.EnsureCaretOnScreenAndTextExpanded()

    /// Move the caret to the position dictated by the given MotionResult value
    member x.MoveCaretToMotionResult (result : MotionResult) =

        // Go ahead and raise any messages associated with a search
        // based motion
        match result.MotionKind with
        | MotionKind.AnySearch searchResult -> x.RaiseSearchResultMessages searchResult
        | MotionKind.AnyWord -> ()
        | MotionKind.Inclusive -> ()
        | MotionKind.Exclusive -> ()

        // Reduce the Span to the line we care about 
        let line = 
            if result.IsForward then SnapshotSpanUtil.GetEndLine result.Span
            else SnapshotSpanUtil.GetStartLine result.Span

        // Get the point which is the last or first point valid on the 
        // particular line / span 
        let getPointFromSpan () = 
            match result.OperationKind with
            | OperationKind.LineWise ->
                if result.IsForward then SnapshotLineUtil.GetEnd line
                else SnapshotLineUtil.GetStart line
            | OperationKind.CharacterWise ->
                if result.IsForward then
                    if result.IsExclusive then result.Span.End
                    else SnapshotPointUtil.GetPreviousPointOnLine result.Span.End 1
                else result.Span.Start

        let point = 
            match result.Column with
            | None -> 
                // No column so just get the information from the span
                getPointFromSpan()
            | Some (CaretColumn.InLastLine col) ->
                let endCol = line |> SnapshotLineUtil.GetEnd |> SnapshotPointUtil.GetColumn
                if col < endCol then 
                    line.Start.Add(col)
                else 
                    getPointFromSpan()
            | Some CaretColumn.AfterLastLine ->
                if result.IsForward then
                    match SnapshotUtil.TryGetLine x.CurrentSnapshot (line.LineNumber + 1) with
                    | None -> 
                        getPointFromSpan()
                    | Some line ->
                        line.Start
                else
                    getPointFromSpan()

        x.MoveCaretToPointAndEnsureVisible point
        _operations.ResetSelection()

    member x.WordUnderCursorOrEmpty =
        let point =  TextViewUtil.GetCaretPoint _textView
        TssUtil.FindCurrentFullWordSpan point WordKind.BigWord
        |> OptionUtil.getOrDefault (SnapshotSpanUtil.CreateEmpty point)
        |> SnapshotSpanUtil.GetText

    member x.NavigateToPoint (point:VirtualSnapshotPoint) = 
        let buf = point.Position.Snapshot.TextBuffer
        if buf = _textView.TextBuffer then 
            TextViewUtil.MoveCaretToPoint _textView point.Position
            TextViewUtil.EnsureCaretOnScreenAndTextExpanded _textView _outlining
            true
        else  _host.NavigateTo point 

    member x.DeleteBlock (col:NormalizedSnapshotSpanCollection) = 
        use edit = _textView.TextBuffer.CreateEdit()
        col |> Seq.iter (fun span -> edit.Delete(span.Span) |> ignore)
        edit.Apply() |> ignore

    member x.DeleteSpan (span:SnapshotSpan) = 
        let buffer = span.Snapshot.TextBuffer
        buffer.Delete(span.Span) |> ignore

    /// Convert the provided whitespace into spaces.  The conversion of tabs into spaces will be 
    /// done based on the TabSize setting
    member x.GetAndNormalizeLeadingWhiteSpaceToSpaces span = 
        let text = 
            span
            |> SnapshotSpanUtil.GetText
            |> Seq.takeWhile CharUtil.IsWhiteSpace
            |> List.ofSeq
        let builder = System.Text.StringBuilder()
        let tabSize = x.TabSize
        for c in text do
            match c with 
            | ' ' -> 
                builder.Append(' ') |> ignore
            | '\t' ->
                // Insert spaces up to the next tab size modulus.  
                let count = 
                    let remainder = builder.Length % tabSize
                    if remainder = 0 then tabSize else remainder
                for i = 1 to count do
                    builder.Append(' ') |> ignore
            | _ -> 
                builder.Append(' ') |> ignore
        builder.ToString(), text.Length

    /// Normalize the whitespace into tabs / spaces based on the ExpandTab,
    /// TabSize settings
    member x.NormalizeWhiteSpace (text : string) = 
        if x.UseSpaces then 
            text
        else
            let tabSize = x.TabSize
            let spacesCount = text.Length % tabSize
            let tabCount = (text.Length - spacesCount) / tabSize 
            let prefix = StringUtil.repeatChar tabCount '\t'
            let suffix = StringUtil.repeatChar spacesCount ' '
            prefix + suffix

    /// Raise the messages for the given SearchResult
    member x.RaiseSearchResultMessages searchResult = 

        match searchResult with 
        | SearchResult.Found (searchData, _, didWrap) ->
            if didWrap then
                let message = 
                    if searchData.Kind.IsAnyForward then Resources.Common_SearchForwardWrapped
                    else Resources.Common_SearchBackwardWrapped
                _statusUtil.OnWarning message
        | SearchResult.NotFound (searchData, isOutsidePath) ->
            let format = 
                if isOutsidePath then
                    match searchData.Kind.Path with
                    | Path.Forward -> Resources.Common_SearchHitBottomWithout
                    | Path.Backward -> Resources.Common_SearchHitTopWithout 
                else
                    Resources.Common_PatternNotFound

            _statusUtil.OnError (format searchData.Pattern)

    /// Shifts a block of lines to the left
    member x.ShiftLineBlockLeft (col: SnapshotSpan seq) multiplier =
        let count = _globalSettings.ShiftWidth * multiplier
        use edit = _textBuffer.CreateEdit()

        col |> Seq.iter (fun span ->
            // Get the span we are formatting within the line.  The span we are given
            // here is the actual span of the selection.  What we want to shift though
            // involves all whitespace from the start of the span through the remainder
            // of the line
            let span = 
                let line = SnapshotPointUtil.GetContainingLine span.Start
                SnapshotSpan(span.Start, line.End)

            let ws, originalLength = x.GetAndNormalizeLeadingWhiteSpaceToSpaces span
            let ws = 
                let length = max (ws.Length - count) 0
                StringUtil.repeatChar length ' ' |> x.NormalizeWhiteSpace
            edit.Replace(span.Start.Position, originalLength, ws) |> ignore)

        edit.Apply() |> ignore

    /// Shift a block of lines to the right
    member x.ShiftLineBlockRight (col: SnapshotSpan seq) multiplier =
        let shiftText = 
            let count = _globalSettings.ShiftWidth * multiplier
            StringUtil.repeatChar count ' '

        use edit = _textBuffer.CreateEdit()

        col |> Seq.iter (fun span ->
            // Get the span we are formatting within the line
            let ws, originalLength = x.GetAndNormalizeLeadingWhiteSpaceToSpaces span
            let ws = x.NormalizeWhiteSpace (ws + shiftText)
            edit.Replace(span.Start.Position, originalLength, ws) |> ignore)

        edit.Apply() |> ignore

    /// Shift lines in the specified range to the left by one shiftwidth
    /// item.  The shift will done against 'column' in the line
    member x.ShiftLineRangeLeft (range : SnapshotLineRange) multiplier =
        let count = _globalSettings.ShiftWidth * multiplier

        use edit = _textBuffer.CreateEdit()
        range.Lines
        |> Seq.iter (fun line ->

            // Get the span we are formatting within the line
            let span = line.Extent
            let ws, originalLength = x.GetAndNormalizeLeadingWhiteSpaceToSpaces span
            let ws = 
                let length = max (ws.Length - count) 0
                StringUtil.repeatChar length ' ' |> x.NormalizeWhiteSpace
            edit.Replace(span.Start.Position, originalLength, ws) |> ignore)
        edit.Apply() |> ignore

    /// Shift lines in the specified range to the right by one shiftwidth 
    /// item.  The shift will occur against column 'column'
    member x.ShiftLineRangeRight (range : SnapshotLineRange) multiplier =
        let shiftText = 
            let count = _globalSettings.ShiftWidth * multiplier
            StringUtil.repeatChar count ' '

        use edit = _textBuffer.CreateEdit()
        range.Lines
        |> Seq.iter (fun line ->

            // Get the span we are formatting within the line
            let span = line.Extent
            let ws, originalLength = x.GetAndNormalizeLeadingWhiteSpaceToSpaces span
            let ws = x.NormalizeWhiteSpace (ws + shiftText)
            edit.Replace(line.Start.Position, originalLength, ws) |> ignore)
        edit.Apply() |> ignore

    /// Convert the provided whitespace into spaces.  The conversion of 
    /// tabs into spaces will be done based on the TabSize setting
    member x.GetAndNormalizeLeadingWhitespaceToSpaces line = 
        let text = 
            line
            |> SnapshotLineUtil.GetText
            |> Seq.takeWhile CharUtil.IsWhiteSpace
            |> List.ofSeq
        let builder = System.Text.StringBuilder()
        let tabSize = x.TabSize
        for c in text do
            match c with 
            | ' ' -> 
                builder.Append(' ') |> ignore
            | '\t' ->
                // Insert spaces up to the next tab size modulus.  
                let count = 
                    let remainder = builder.Length % tabSize
                    if remainder = 0 then tabSize else remainder
                for i = 1 to count do
                    builder.Append(' ') |> ignore
            | _ -> 
                builder.Append(' ') |> ignore
        builder.ToString(), text.Length

    member x.Join (range:SnapshotLineRange) kind = 

        if range.Count > 1 then

            use edit = _data.TextView.TextBuffer.CreateEdit()

            let replace = 
                match kind with
                | JoinKind.KeepEmptySpaces -> ""
                | JoinKind.RemoveEmptySpaces -> " "

            // First delete line breaks on all but the last line 
            range.Lines
            |> Seq.take (range.Count - 1)
            |> Seq.iter (fun line -> 

                // Delete the line break span
                let span = line |> SnapshotLineUtil.GetLineBreakSpan |> SnapshotSpanUtil.GetSpan
                edit.Replace(span, replace) |> ignore )

            // Remove the empty spaces from the start of all but the first line 
            // if the option was specified
            match kind with
            | JoinKind.KeepEmptySpaces -> ()
            | JoinKind.RemoveEmptySpaces ->
                range.Lines 
                |> Seq.skip 1
                |> Seq.iter (fun line ->
                        let count =
                            line.Extent 
                            |> SnapshotSpanUtil.GetText
                            |> Seq.takeWhile CharUtil.IsWhiteSpace
                            |> Seq.length
                        if count > 0 then
                            edit.Delete(line.Start.Position,count) |> ignore )

            // Now position the caret on the new snapshot
            edit.Apply() |> ignore

    member x.UpdateRegister (reg:Register) regOperation value = 
        _registerMap.SetRegisterValue reg regOperation value

    // Puts the provided StringData at the given point in the ITextBuffer.  Does not attempt
    // to move the caret as a result of this operation
    member x.Put point stringData opKind =

        match stringData with
        | StringData.Simple str -> 

            // Simple strings can go directly in at the position.  Need to adjust the text if 
            // we are inserting at the end of the buffer
            let text = 
                let newLine = EditUtil.NewLine _options
                match opKind with
                | OperationKind.LineWise -> 
                    if SnapshotPointUtil.IsEndPoint point then
                        // At the end of the file so we need to manipulate the new line character
                        // a bit.  It's typically at the end of the line but at the end of the 
                        // ITextBuffer we need it to be at the beginning since there is no newline 
                        // to append after at the end of the buffer.  
                        let str = EditUtil.RemoveEndingNewLine str
                        newLine + str
                    elif not (SnapshotPointUtil.IsStartOfLine point) then
                        // Edit in the middle of the line.  Need to prefix the string with a newline
                        // in order to correctly insert here.  
                        //
                        // This type of put will occur when a linewise register value is inserted 
                        // from a visual mode character selection
                        newLine + str
                    elif not (EditUtil.EndsWithNewLine str) then
                        // All other linewise operation should have a trailing newline to ensure a
                        // new line is actually inserted
                        str + newLine
                    else
                        str
                | OperationKind.CharacterWise ->
                    // Simplest insert.  No changes needed
                    str

            _textBuffer.Insert(point.Position, text) |> ignore

        | StringData.Block col -> 

            use edit = _textBuffer.CreateEdit()

            match opKind with
            | OperationKind.CharacterWise ->

                // Collection strings are inserted at the original character
                // position down the set of lines creating whitespace as needed
                // to match the indent
                let lineNumber, column = SnapshotPointUtil.GetLineColumn point
    
                // First break the strings into the collection to edit against
                // existing lines and those which need to create new lines at
                // the end of the buffer
                let originalSnapshot = point.Snapshot
                let insertCol, appendCol = 
                    let lastLineNumber = SnapshotUtil.GetLastLineNumber originalSnapshot
                    let insertCount = min ((lastLineNumber - lineNumber) + 1) col.Count
                    (Seq.take insertCount col, Seq.skip insertCount col)
    
                // Insert the text at existing lines
                insertCol |> Seq.iteri (fun offset str -> 
                    let line = originalSnapshot.GetLineFromLineNumber (offset+lineNumber)
                    if line.Length < column then
                        let prefix = String.replicate (column - line.Length) " "
                        edit.Insert(line.Start.Position, prefix + str) |> ignore
                    else
                        edit.Insert(line.Start.Position + column, str) |> ignore)
    
                // Add the text to the end of the buffer.
                if not (Seq.isEmpty appendCol) then
                    let prefix = (EditUtil.NewLine _options) + (String.replicate column " ")
                    let text = Seq.fold (fun text str -> text + prefix + str) "" appendCol
                    let endPoint = SnapshotUtil.GetEndPoint originalSnapshot
                    edit.Insert(endPoint.Position, text) |> ignore

            | OperationKind.LineWise ->

                // Strings are inserted line wise into the ITextBuffer.  Build up an
                // aggregate string and insert it here
                let text = col |> Seq.fold (fun state elem -> state + elem + (EditUtil.NewLine _options)) StringUtil.empty

                edit.Insert(point.Position, text) |> ignore

            edit.Apply() |> ignore

    member x.Beep() = if not _settings.GlobalSettings.VisualBell then _host.Beep()

    member x.DoWithOutlining func = 
        match _outlining with
        | None -> x.Beep()
        | Some(outlining) -> func outlining

    member x.CheckDirty func = 
        if _host.IsDirty _textView.TextBuffer then 
            _statusUtil.OnError Resources.Common_NoWriteSinceLastChange
        else
            func()

    /// Undo 'count' operations in the ITextBuffer and ensure the caret is on the screen
    /// after the undo completes
    member x.Undo count = 
        _undoRedoOperations.Undo count
        x.EnsureCaretOnScreenAndTextExpanded()

    /// Redo 'count' operations in the ITextBuffer and ensure the caret is on the screen
    /// after the redo completes
    member x.Redo count = 
        _undoRedoOperations.Redo count
        x.EnsureCaretOnScreenAndTextExpanded()

    /// Ensure the caret is visible on the screen.  Will not expand the text around the caret
    /// if it's in the middle of a collapsed region
    member x.EnsureCaretOnScreen () = 
        TextViewUtil.EnsureCaretOnScreen _textView 

    /// Ensure the caret is visible on the screen and any regions surrounding the caret are
    /// expanded such that the actual caret is visible
    member x.EnsureCaretOnScreenAndTextExpanded () = 
        TextViewUtil.EnsureCaretOnScreenAndTextExpanded _textView _outlining

    /// Ensure the point is visible on the screen and any regions surrounding the point are
    /// expanded such that the actual caret is visible
    member x.EnsurePointOnScreenAndTextExpanded point = 
        _host.EnsureVisible _textView point
        match _outlining with
        | None -> ()
        | Some(outlining) -> outlining.ExpandAll(SnapshotSpan(point,0), fun _ -> true) |> ignore

    interface ICommonOperations with
        member x.TextView = _textView 
        member x.TabSize = x.TabSize
        member x.UseSpaces = x.UseSpaces
        member x.EditorOperations = _operations
        member x.EditorOptions = _options
        member x.FoldManager = _data.FoldManager
        member x.SearchService = _data.SearchService
        member x.UndoRedoOperations = _data.UndoRedoOperations

        member x.Join range kind = x.Join range kind
        member x.GoToDefinition () = 
            let before = TextViewUtil.GetCaretPoint _textView
            if _host.GoToDefinition() then
                _jumpList.Add before |> ignore
                Succeeded
            else
                match TssUtil.FindCurrentFullWordSpan _textView.Caret.Position.BufferPosition Vim.WordKind.BigWord with
                | Some(span) -> 
                    let msg = Resources.Common_GotoDefFailed (span.GetText())
                    Failed(msg)
                | None ->  Failed(Resources.Common_GotoDefNoWordUnderCursor) 

        member x.RaiseSearchResultMessages searchResult = x.RaiseSearchResultMessages searchResult
        member x.SetMark point c (markMap : IMarkMap) = 
            if System.Char.IsLetter(c) || c = '\'' || c = '`' then
                markMap.SetMark point c
                Succeeded
            else
                Failed(Resources.Common_MarkInvalid)

        member x.NavigateToPoint point = x.NavigateToPoint point
                
        member x.JumpToMark ident (map:IMarkMap) = 
            let before = TextViewUtil.GetCaretPoint _textView
            let jumpLocal (point:VirtualSnapshotPoint) = 
                TextViewUtil.MoveCaretToPoint _textView point.Position
                TextViewUtil.EnsureCaretOnScreenAndTextExpanded _textView _outlining
                _jumpList.Add before |> ignore
                Succeeded
            if not (map.IsLocalMark ident) then 
                match map.GetGlobalMark ident with
                | None -> Failed Resources.Common_MarkNotSet
                | Some(point) -> 
                    match x.NavigateToPoint point with
                    | true -> 
                        _jumpList.Add before |> ignore
                        Succeeded
                    | false -> Failed Resources.Common_MarkInvalid
            else 
                match map.GetLocalMark _textView.TextBuffer ident with
                | Some(point) -> jumpLocal point
                | None -> Failed Resources.Common_MarkNotSet

        /// Move the cursor count spaces left
        member x.MoveCaretLeft count = 
            let caret = TextViewUtil.GetCaretPoint _textView
            let leftPoint = SnapshotPointUtil.GetPreviousPointOnLine caret count
            if caret <> leftPoint then
                _operations.ResetSelection()
                TextViewUtil.MoveCaretToPoint _textView leftPoint
    
        /// Move the cursor count spaces to the right
        member x.MoveCaretRight count =
            let caret = TextViewUtil.GetCaretPoint _textView
            let doMove point = 
                if point <> caret then
                    _operations.ResetSelection()
                    TextViewUtil.MoveCaretToPoint _textView point

            if SnapshotPointUtil.IsLastPointOnLine caret then

                // If we are an the last point of the line then only move if VirtualEdit=onemore
                let line = SnapshotPointUtil.GetContainingLine caret
                if _settings.GlobalSettings.IsVirtualEditOneMore && line.Length > 0 then 
                    doMove line.End
            else

                let rightPoint = SnapshotPointUtil.GetNextPointOnLine caret count
                doMove rightPoint
    
        /// Move the cursor count spaces up 
        member x.MoveCaretUp count =
            let caret = TextViewUtil.GetCaretPoint _textView
            let current = caret.GetContainingLine()
            let count = 
                if current.LineNumber - count > 0 then count
                else current.LineNumber 
            if count > 0 then _operations.ResetSelection()
            for i = 1 to count do   
                _operations.MoveLineUp(false)
            x.MoveCaretForVirtualEdit()

        /// Move the cursor count spaces down
        member x.MoveCaretDown count =
            let caret = TextViewUtil.GetCaretPoint _textView
            let line = caret.GetContainingLine()
            let tss = line.Snapshot
            let count = 
                if line.LineNumber + count < tss.LineCount then count
                else (tss.LineCount - line.LineNumber) - 1 
            if count > 0 then _operations.ResetSelection()
            for i = 1 to count do
                _operations.MoveLineDown(false)
            x.MoveCaretForVirtualEdit()

        member x.MoveWordForward kind count = 
            let caret = TextViewUtil.GetCaretPoint _textView
            let pos = TssUtil.FindNextWordStart caret count kind
            TextViewUtil.MoveCaretToPoint _textView pos 
            
        member x.MoveWordBackward kind count = 
            let caret = TextViewUtil.GetCaretPoint _textView
            let pos = TssUtil.FindPreviousWordStart caret count kind
            TextViewUtil.MoveCaretToPoint _textView pos 

        member x.MoveCaretForVirtualEdit () = x.MoveCaretForVirtualEdit()

        member x.InsertText text count = 
            let text = StringUtil.repeat count text
            let point = TextViewUtil.GetCaretPoint _textView
            use edit = _textView.TextBuffer.CreateEdit()
            edit.Insert(point.Position, text) |> ignore
            edit.Apply() |> ignore
             
            // Need to adjust the caret to the end of the inserted text.  Very important
            // for operations like repeat
            if not (StringUtil.isNullOrEmpty text) then
                let snapshot = _textView.TextSnapshot
                let position = point.Position + text.Length - 1 
                let caret = SnapshotPoint(snapshot, position)
                _textView.Caret.MoveTo(caret) |> ignore
                _textView.Caret.EnsureVisible()

        member x.MoveCaretAndScrollLines dir count =
            let lines = _settings.Scroll
            let tss = _textView.TextSnapshot
            let caretPoint = TextViewUtil.GetCaretPoint _textView
            let curLine = caretPoint.GetContainingLine().LineNumber
            let newLine = 
                match dir with
                | ScrollDirection.Down -> min (tss.LineCount - 1) (curLine + lines)
                | ScrollDirection.Up -> max (0) (curLine - lines)
                | _ -> failwith "Invalid enum value"
            let newCaret = tss.GetLineFromLineNumber(newLine).Start
            _operations.ResetSelection()
            _textView.Caret.MoveTo(newCaret) |> ignore
            _textView.Caret.EnsureVisible()

        member x.ScrollLines dir count =
            for i = 1 to count do
                match dir with
                | ScrollDirection.Down -> _operations.ScrollDownAndMoveCaretIfNecessary()
                | ScrollDirection.Up -> _operations.ScrollUpAndMoveCaretIfNecessary()
                | _ -> failwith "Invalid enum value"
    
        member x.ScrollPages dir count = 
            let func,getLine =
                match dir with
                | ScrollDirection.Down -> (_operations.ScrollPageDown, fun () -> _textView.TextViewLines.LastVisibleLine)
                | ScrollDirection.Up -> (_operations.ScrollPageUp, fun () -> _textView.TextViewLines.FirstVisibleLine)
                | _ -> failwith "Invalid enum value"
            for i = 1 to count do
                func()

            // Scrolling itself does not move the caret.  Must be manually moved
            let line = getLine()
            _textView.Caret.MoveTo(line) |> ignore

        member x.ShiftLineBlockLeft col multiplier = x.ShiftLineBlockLeft col multiplier
        member x.ShiftLineBlockRight col multiplier = x.ShiftLineBlockRight col multiplier
        member x.ShiftLineRangeLeft range multiplier = x.ShiftLineRangeLeft range multiplier
        member x.ShiftLineRangeRight range multiplier = x.ShiftLineRangeRight range multiplier

        member x.Undo count = x.Undo count
        member x.Redo count = x.Redo count
        member x.Save() = _host.Save _textView.TextBuffer
        member x.SaveAs fileName = 
            let text = SnapshotUtil.GetText _textView.TextSnapshot
            _host.SaveTextAs text fileName
        member x.SaveAll() = _host.SaveAllFiles()
        member x.Close checkDirty = _host.Close _textView checkDirty
        member x.CloseAll checkDirty = _host.CloseAllFiles checkDirty
        member x.GoToNextTab direction count = _host.GoToNextTab direction count
        member x.GoToTab index = _host.GoToTab index
        member x.EnsureCaretOnScreen () = x.EnsureCaretOnScreen()
        member x.EnsureCaretOnScreenAndTextExpanded () = x.EnsureCaretOnScreenAndTextExpanded()
        member x.EnsurePointOnScreenAndTextExpanded point = x.EnsurePointOnScreenAndTextExpanded point
        member x.MoveCaretToPoint point =  TextViewUtil.MoveCaretToPoint _textView point 
        member x.MoveCaretToPointAndEnsureVisible point = x.MoveCaretToPointAndEnsureVisible point
        member x.MoveCaretToMotionResult data = x.MoveCaretToMotionResult data
        member x.Beep () = x.Beep()

        member x.OpenFold span count = 
            x.DoWithOutlining (fun outlining ->
                let regions = outlining.GetCollapsedRegions(span) |> Seq.truncate count
                if Seq.isEmpty regions then _statusUtil.OnError Resources.Common_NoFoldFound
                else  regions |> Seq.iter (fun x -> outlining.Expand(x) |> ignore ))

        member x.OpenAllFolds span =
            x.DoWithOutlining (fun outlining ->
                let regions = outlining.GetCollapsedRegions(span) 
                if Seq.isEmpty regions then _statusUtil.OnError Resources.Common_NoFoldFound
                else  regions |> Seq.iter (fun x -> outlining.Expand(x) |> ignore ))

        member x.CloseFold span count = 
            x.DoWithOutlining (fun outlining ->
                let pos = span |> SnapshotSpanUtil.GetStartPoint |> SnapshotPointUtil.GetPosition
                let temp = 
                    outlining.GetAllRegions(span) 
                    |> Seq.filter (fun x -> not (x.IsCollapsed))
                    |> Seq.map (fun x -> (TrackingSpanUtil.GetSpan _textView.TextSnapshot x.Extent) ,x)
                    |> SeqUtil.filterToSome2
                    |> Seq.sortBy (fun (span,_) -> pos - span.Start.Position )
                    |> List.ofSeq
                let regions = temp  |> Seq.truncate count
                if Seq.isEmpty regions then _statusUtil.OnError Resources.Common_NoFoldFound
                else regions |> Seq.iter (fun (_,x) -> outlining.TryCollapse(x) |> ignore))

        member x.CloseAllFolds span =
            x.DoWithOutlining (fun outlining ->
                let regions = outlining.GetAllRegions(span) 
                if Seq.isEmpty regions then _statusUtil.OnError Resources.Common_NoFoldFound
                else  regions |> Seq.iter (fun x -> outlining.TryCollapse(x) |> ignore ))

        member x.FoldLines count = 
            if count > 1 then 
                let caretLine = TextViewUtil.GetCaretLine _textView
                let range = SnapshotLineRangeUtil.CreateForLineAndMaxCount caretLine count
                _data.FoldManager.CreateFold range

        member x.FormatLines range =
            _host.FormatLines _textView range

        member x.DeleteOneFoldAtCursor () = 
            let point = TextViewUtil.GetCaretPoint _textView
            if not ( _data.FoldManager.DeleteFold point ) then
                _statusUtil.OnError Resources.Common_NoFoldFound

        member x.DeleteAllFoldsAtCursor () =
            let deleteAtCaret () = 
                let point = TextViewUtil.GetCaretPoint _textView
                _data.FoldManager.DeleteFold point
            if not (deleteAtCaret()) then
                _statusUtil.OnError Resources.Common_NoFoldFound
            else
                while deleteAtCaret() do
                    // Keep on deleteing 
                    ()

        member x.Substitute pattern replace (range:SnapshotLineRange) flags = 

            /// Actually do the replace with the given regex
            let doReplace (regex:VimRegex) = 
                use edit = _textView.TextBuffer.CreateEdit()

                let replaceOne (span:SnapshotSpan) (c:Capture) = 
                    let newText =  regex.Replace c.Value replace 1
                    let offset = span.Start.Position
                    edit.Replace(Span(c.Index+offset, c.Length), newText) |> ignore
                let getMatches (span:SnapshotSpan) = 
                    if Util.IsFlagSet flags SubstituteFlags.ReplaceAll then
                        regex.Regex.Matches(span.GetText()) |> Seq.cast<Match>
                    else
                        regex.Regex.Match(span.GetText()) |> Seq.singleton
                let matches = 
                    range.Lines
                    |> Seq.map (fun line -> line.ExtentIncludingLineBreak)
                    |> Seq.map (fun span -> getMatches span |> Seq.map (fun m -> (m,span)) )
                    |> Seq.concat 
                    |> Seq.filter (fun (m,_) -> m.Success)

                if not (Util.IsFlagSet flags SubstituteFlags.ReportOnly) then
                    // Actually do the edits
                    matches |> Seq.iter (fun (m,span) -> replaceOne span m)

                // Update the status for the substitute operation
                let printMessage () = 

                    // Get the replace message for multiple lines
                    let replaceMessage = 
                        let replaceCount = matches |> Seq.length
                        let lineCount = 
                            matches 
                            |> Seq.map (fun (_,s) -> s.Start.GetContainingLine().LineNumber)
                            |> Seq.distinct
                            |> Seq.length
                        if replaceCount > 1 then Resources.Common_SubstituteComplete replaceCount lineCount |> Some
                        else None

                    let printReplaceMessage () =
                        match replaceMessage with 
                        | None -> ()
                        | Some(msg) -> _statusUtil.OnStatus msg

                    // Find the last line in the replace sequence.  This is printed out to the 
                    // user and needs to represent the current state of the line, not the previous
                    let lastLine = 
                        if Seq.isEmpty matches then 
                            None
                        else 
                            let _, span = matches |> SeqUtil.last 
                            let tracking = span.Snapshot.CreateTrackingSpan(span.Span, SpanTrackingMode.EdgeInclusive)
                            match TrackingSpanUtil.GetSpan _data.TextView.TextSnapshot tracking with
                            | None -> None
                            | Some(span) -> SnapshotSpanUtil.GetStartLine span |> Some

                    // Now consider the options 
                    match lastLine with 
                    | None -> printReplaceMessage()
                    | Some(line) ->

                        let printBoth msg = 
                            match replaceMessage with
                            | None -> _statusUtil.OnStatus msg
                            | Some(replaceMessage) -> _statusUtil.OnStatusLong [replaceMessage; msg]

                        if Util.IsFlagSet flags SubstituteFlags.PrintLast then
                            printBoth (line.GetText())
                        elif Util.IsFlagSet flags SubstituteFlags.PrintLastWithNumber then
                            sprintf "  %d %s" (line.LineNumber+1) (line.GetText()) |> printBoth 
                        elif Util.IsFlagSet flags SubstituteFlags.PrintLastWithList then
                            sprintf "%s$" (line.GetText()) |> printBoth 
                        else printReplaceMessage()

                if edit.HasEffectiveChanges then
                    edit.Apply() |> ignore                                
                    printMessage()
                elif Util.IsFlagSet flags SubstituteFlags.ReportOnly then
                    edit.Cancel()
                    printMessage ()
                elif Util.IsFlagSet flags SubstituteFlags.SuppressError then
                    edit.Cancel()
                else 
                    edit.Cancel()
                    _statusUtil.OnError (Resources.Common_PatternNotFound pattern)

            match _regexFactory.CreateForSubstituteFlags pattern flags with
            | None -> 
                _statusUtil.OnError (Resources.Common_PatternNotFound pattern)
            | Some (regex) -> 
                doReplace regex

                // Make sure to update the saved state.  Note that there are 2 patterns stored 
                // across buffers.
                //
                // 1. Last substituted pattern
                // 2. Last searched for pattern.
                //
                // A substitute command should update both of them 
                _vimData.LastSubstituteData <- Some { SearchPattern=pattern; Substitute=replace; Flags=flags}
                _vimData.LastPatternData <- { Pattern = pattern; Path = Path.Forward }


        member x.UpdateRegister reg regOp editSpan opKind = 
            let value = RegisterValue.String (StringData.OfEditSpan editSpan, opKind)
            x.UpdateRegister reg regOp value
        member x.UpdateRegisterForValue reg regOp value = 
            x.UpdateRegister reg regOp value
        member x.UpdateRegisterForSpan reg regOp span opKind = 
            let value = RegisterValue.String (StringData.OfSpan span, opKind)
            x.UpdateRegister reg regOp value
        member x.UpdateRegisterForCollection reg regOp col opKind = 
            let value = RegisterValue.String (StringData.OfNormalizedSnasphotSpanCollection col, opKind)
            x.UpdateRegister reg regOp value

        member x.GoToLocalDeclaration() = 
            if not (_host.GoToLocalDeclaration _textView x.WordUnderCursorOrEmpty) then _host.Beep()

        member x.GoToGlobalDeclaration () = 
            if not (_host.GoToGlobalDeclaration _textView x.WordUnderCursorOrEmpty) then _host.Beep()

        member x.GoToFile () = 
            x.CheckDirty (fun () ->
                let text = x.WordUnderCursorOrEmpty 
                match _host.LoadFileIntoExistingWindow text _textBuffer with
                | HostResult.Success -> ()
                | HostResult.Error(_) -> _statusUtil.OnError (Resources.NormalMode_CantFindFile text))

        /// Look for a word under the cursor and go to the specified file in a new window.  No need to 
        /// check for dirty since we are opening a new window
        member x.GoToFileInNewWindow () =
            let text = x.WordUnderCursorOrEmpty 
            match _host.LoadFileIntoNewWindow text with
            | HostResult.Success -> ()
            | HostResult.Error(_) -> _statusUtil.OnError (Resources.NormalMode_CantFindFile text)

        member x.Put point stringData opKind = x.Put point stringData opKind



