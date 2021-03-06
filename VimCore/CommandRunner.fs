﻿#light

namespace Vim
open Microsoft.VisualStudio.Text.Editor

type internal CommandRunnerData = {

    /// Name of the current command
    KeyInputSet : KeyInputSet

    /// Reverse ordered List of all KeyInput for a given command
    Inputs : KeyInput list

    /// Once a CommandBinding is chosen this will be the flags of that 
    /// CommandBinding instance
    CommandFlags : CommandFlags option
}

/// Implementation of the ICommandRunner interface.  
type internal CommandRunner
    ( 
        _textView : ITextView,
        _registerMap : IRegisterMap,
        _capture : IMotionCapture,
        _commandUtil : ICommandUtil,
        _statusUtil : IStatusUtil,
        _visualKind : VisualKind ) =

    /// Represents the empty state for processing commands.  Holds all of the default
    /// values
    let _emptyData = { 
        KeyInputSet = Empty
        Inputs = List.empty
        CommandFlags = None
    }

    let _commandRanEvent = Event<_>()
    
    let mutable _commandMap : Map<KeyInputSet, CommandBinding> = Map.empty

    /// Contains all of the state data for a Command operation
    let mutable _data = _emptyData

    /// The latest BindData we are waiting to receive KeyInput to complete
    let mutable _runBindData : BindData<Command * CommandBinding> option = None

    /// True during the running of a particular KeyInput 
    let mutable _inBind = false

    /// Try and get the VisualSpan for the provided kind
    member x.TryGetVisualSpan kind = 
        match kind with
        | VisualKind.Character -> 
            let visualSpan = VisualSpan.Character (_textView.Selection.StreamSelectionSpan.SnapshotSpan)
            Some visualSpan
        | VisualKind.Line-> 
            let visualSpan = VisualSpan.Line (_textView.Selection.StreamSelectionSpan.SnapshotSpan |> SnapshotLineRangeUtil.CreateForSpan)
            Some visualSpan
        | VisualKind.Block -> 
            let col = _textView.Selection.SelectedSpans
            if col.Count = 0 then
                // Shouldn't be possible but needs to be accounted for.  If there are 0 selected 
                // spans then what are we do with them?  Another possibility here would be to create
                // a single empty span at the caret.  
                None
            else
                let col = NonEmptyCollection(col.[0], col |> Seq.skip 1 |> List.ofSeq)
                VisualSpan.Block col |> Some

    /// Used to wait for the character after the " which signals the Register.  When the register
    /// is found it will be passed to completeFunc
    member x.BindRegister completeFunc = 

        let inner (keyInput : KeyInput) = 
            match RegisterNameUtil.CharToRegister keyInput.Char with
            | None -> BindResult.Error
            | Some name -> completeFunc name

        BindResult.NeedMoreInput { KeyRemapMode = None; BindFunction = inner }

    /// Used to wait for a count value to complete.  Passed in the initial digit 
    /// in the count
    member x.BindCount (initialDigit : KeyInput) completeFunc =

        let rec inner (num:string) (ki:KeyInput) = 
            if ki.IsDigit then
                let num = num + ki.Char.ToString()
                BindResult.NeedMoreInput { KeyRemapMode = None; BindFunction = inner num }
            else
                let count = System.Int32.Parse(num)
                completeFunc count ki

        inner StringUtil.empty initialDigit 

    /// Bind the optional register and count operations and then pass that off to the command 
    /// infrastructure
    member x.BindCountAndRegister (keyInput : KeyInput) =

        let tryRegister (keyInput : KeyInput) foundFunc missingFunc = 
            if keyInput.Char = '"' then x.BindRegister foundFunc
            else missingFunc keyInput

        let tryCount (keyInput : KeyInput) foundFunc missingFunc = 
            if keyInput.IsDigit && keyInput.Char <> '0' then x.BindCount keyInput foundFunc
            else missingFunc keyInput

        // Okay now we get to build some fun.  It's possible to enter register and count
        // in either order with either being missing.  So we need to try all of the possibilities
        // here

        let bindCommandResult register count =
            let func = x.BindCommand register count
            let data = { KeyRemapMode = None; BindFunction = func }
            BindResult.NeedMoreInput data

        let tryCountThenRegister keyInput missingCount = 
            let foundCount count keyInput =
                let count = Some count
                tryRegister keyInput (fun register -> bindCommandResult (Some register) count) (fun keyInput -> x.BindCommand None count keyInput)

            tryCount keyInput foundCount missingCount

        let tryRegisterThenCount keyInput missingRegister = 

            let foundRegister register = 
                let register = Some register
                let next keyInput = tryCount keyInput (fun count keyInput -> x.BindCommand register (Some count) keyInput) (fun keyInput -> x.BindCommand register None keyInput)
                BindResult.NeedMoreInput { KeyRemapMode = None; BindFunction = next }

            tryRegister keyInput foundRegister missingRegister

        // Now we put it altogether
        tryRegisterThenCount keyInput (fun keyInput -> tryCountThenRegister keyInput (fun keyInput -> x.BindCommand None None keyInput))

    /// Wait for a motion binding to be complete
    member x.BindMotion keyInput completeFunc =

        let result = _capture.GetOperatorMotion keyInput
        result.Convert completeFunc

    /// Bind the Command instance
    member x.BindCommand registerName count keyInput = 

        // Find any commands which have the given prefix
        let findPrefixMatches (commandName : KeyInputSet) =
            let commandInputs = commandName.KeyInputs
            let count = List.length commandInputs
            let commandInputsSeq = commandInputs |> Seq.ofList
            _commandMap
            |> Seq.map (fun pair -> pair.Value)
            |> Seq.filter (fun command -> command.KeyInputSet.KeyInputs.Length >= count)
            |> Seq.filter (fun command -> 
                let short = command.KeyInputSet.KeyInputs |> Seq.ofList |> Seq.take count
                SeqUtil.contentsEqual commandInputsSeq short)

        let commandData = { Count = count; RegisterName = registerName }
        let register = commandData.GetRegister _registerMap

        let rec inner (commandName : KeyInputSet) previousCommandName currentInput = 

            // Used to continue driving the 'inner' function a BindData value.
            let bindNext keyRemapMode = 
                let inner keyInput = 
                    let previousCommandName = commandName
                    let commandName = previousCommandName.Add keyInput
                    inner commandName previousCommandName keyInput
                BindResult.NeedMoreInput { KeyRemapMode = keyRemapMode ; BindFunction = inner }

            // Used to complete the transition from a LegacyMotionCommand to a NormalCommand
            let completeMotion commandBinding convertFunc (motion, motionCount) =
                let argument = { MotionContext = MotionContext.AfterOperator; OperatorCount = count; MotionCount = motionCount }
                let data = { Motion = motion; MotionArgument = argument }
                let command = convertFunc data
                (Command.NormalCommand (command, commandData), commandBinding)

            match Map.tryFind commandName _commandMap with
            | Some(commandBinding) ->
                match commandBinding with
                | CommandBinding.LegacyBinding (_, _, func) -> 
                    let func () = func count (commandData.GetRegister _registerMap)
                    let data = LegacyData(func)
                    BindResult.Complete (Command.LegacyCommand data, commandBinding)
                | CommandBinding.NormalBinding (_, _, normalCommand) -> 
                    BindResult.Complete (Command.NormalCommand (normalCommand, commandData), commandBinding)
                | CommandBinding.LegacyVisualBinding (_, _, kind, func) -> 
                    match x.TryGetVisualSpan kind with
                    | None ->
                        _statusUtil.OnError Resources.Common_SelectionInvalid
                        BindResult.Error
                    | Some visualSpan ->
                        let func () = func count (commandData.GetRegister _registerMap) visualSpan
                        let data = LegacyData(func)
                        BindResult.Complete (Command.LegacyCommand data, commandBinding)
                | CommandBinding.VisualBinding (_, _, visualCommand) ->
                    match x.TryGetVisualSpan _visualKind with
                    | None -> 
                        _statusUtil.OnError Resources.Common_SelectionInvalid
                        BindResult.Error
                    | Some visualSpan -> 
                        let visualCommand = Command.VisualCommand (visualCommand, commandData, visualSpan)
                        BindResult.Complete (visualCommand, commandBinding)
                | CommandBinding.MotionBinding (_, _, func) -> 
                    // Can't just call this.  It's possible there is a non-motion command with a 
                    // longer command commandInputs.  If there are any other commands which have a 
                    // matching prefix we can't bind to the command yet
                    let withPrefix = 
                        findPrefixMatches commandName
                        |> Seq.filter (fun c -> c.KeyInputSet <> commandBinding.KeyInputSet)
                    if Seq.isEmpty withPrefix then 
                        // Nothing else matched so we are good to go for this motion.
                        _data <- { _data with CommandFlags = Some commandBinding.CommandFlags }
                        BindResult<_>.CreateNeedMoreInput (Some KeyRemapMode.OperatorPending) (fun keyInput -> x.BindMotion keyInput (completeMotion commandBinding func))
                    else 
                        // At least one other command matched so we need at least one more piece of input to
                        // differentiate the commands.  At this point though because the command is of the
                        // motion variety we are in operator pending
                        _data <- { _data with CommandFlags = Some commandBinding.CommandFlags }
                        bindNext (Some KeyRemapMode.OperatorPending)
    
                | CommandBinding.ComplexNormalBinding (_, _, bindDataStorage) -> 
                    _data <- { _data with CommandFlags = Some commandBinding.CommandFlags }
                    let bindData = bindDataStorage.CreateBindData()
                    let bindData = bindData.Convert (fun normalCommand -> (Command.NormalCommand (normalCommand, commandData), commandBinding))
                    BindResult.NeedMoreInput bindData
                | CommandBinding.ComplexVisualBinding (_, _, bindDataStorage) -> 
                    _data <- { _data with CommandFlags = Some commandBinding.CommandFlags }
                    let bindData = bindDataStorage.CreateBindData()
                    let bindData = bindData.Map (fun visualCommand -> 
                        match x.TryGetVisualSpan _visualKind with
                        | None ->
                            _statusUtil.OnError Resources.Common_SelectionInvalid
                            BindResult.Error
                        | Some visualSpan ->
                            let visualCommand = Command.VisualCommand (visualCommand, commandData, visualSpan)
                            BindResult.Complete (visualCommand, commandBinding))
                    BindResult.NeedMoreInput bindData
            | None -> 
                let hasPrefixMatch = findPrefixMatches commandName |> SeqUtil.isNotEmpty
                if commandName.KeyInputs.Length > 1 && not hasPrefixMatch then
    
                    // It's possible to have 2 comamnds with similar prefixes where one of them is a 
                    // LegacyMotionCommand.  Consider
                    //
                    //  g~{motion}
                    //  g~g~
                    //
                    // This code path triggers when we get the first character after the motion 
                    // command name.  If the new name isn't the prefix of any other command then we can
                    // choose the motion 
                    match Map.tryFind previousCommandName _commandMap with
                    | Some(command) ->
                        match command with
                        | CommandBinding.LegacyBinding _ -> 
                            bindNext None
                        | CommandBinding.LegacyVisualBinding _ -> 
                            bindNext None
                        | CommandBinding.MotionBinding (_, _, func) -> 
                            _data <- { _data with CommandFlags = Some command.CommandFlags }
                            x.BindMotion currentInput (completeMotion command func)
                        | CommandBinding.NormalBinding _ -> 
                            bindNext None
                        | CommandBinding.VisualBinding _ -> 
                            bindNext None
                        | CommandBinding.ComplexNormalBinding _ -> 
                            bindNext None
                        | CommandBinding.ComplexVisualBinding _ -> 
                            bindNext None
                    | None -> 
                        // No prefix matches and no previous motion so won't ever match a comamand
                        BindResult.Error
                elif hasPrefixMatch then
                    // At least one command with a prefix matching the current input.  Wait for the 
                    // next keystroke
                    bindNext None
                else
                    // No prospect of matching a command at this point
                    BindResult.Error

        // Lets get it started
        inner (KeyInputSet.OneKeyInput keyInput) KeyInputSet.Empty keyInput

    /// Should the Esacpe key cancel the current command
    member x.ShouldEscapeCancelCurrentCommand () = 
        match _data.CommandFlags with
        | None -> true
        | Some flags -> not (Util.IsFlagSet flags CommandFlags.HandlesEscape)

    /// Function which handles all incoming input
    member x.Run (ki:KeyInput) =
        if ki = KeyInputUtil.EscapeKey && x.ShouldEscapeCancelCurrentCommand() then 
            x.ResetState()
            BindResult.Cancelled
        elif _inBind then 
            // If we're in the middle of binding the previous value then error.  We don't
            // support re-entrancy while binding
            BindResult.Error
        else
            _data <- {_data with Inputs = ki :: _data.Inputs }
            let result = 
                _inBind <- true
                try
                    match _runBindData with
                    | Some bindData -> bindData.BindFunction ki
                    | None -> x.BindCountAndRegister ki
                finally 
                    _inBind <-  false

            match result with
            | BindResult.Complete (command, commandBinding) -> 
                x.ResetState()
                let result = _commandUtil.RunCommand command
                let data = { Command = command; CommandBinding = commandBinding; CommandResult = result }
                _commandRanEvent.Trigger data
                BindResult.Complete data
            | BindResult.Cancelled ->
                x.ResetState()
                BindResult.Error
            | BindResult.Error ->
                x.ResetState()
                BindResult.Error
            | BindResult.NeedMoreInput bindData ->
                _runBindData <- Some bindData
                BindResult.NeedMoreInput { KeyRemapMode = bindData.KeyRemapMode; BindFunction = x.Run }
            
    member x.Add (command : CommandBinding) = 
        if Map.containsKey command.KeyInputSet _commandMap then 
            invalidArg "command" Resources.CommandRunner_CommandNameAlreadyAdded
        _commandMap <- Map.add command.KeyInputSet command _commandMap
    member x.Remove (name:KeyInputSet) = _commandMap <- Map.remove name _commandMap
    member x.ResetState () =
        _data <- _emptyData
        _runBindData <- None

    interface ICommandRunner with
        member x.Commands = _commandMap |> Seq.map (fun pair -> pair.Value)
        member x.IsHandlingEscape =
            match _data.CommandFlags with
            | None -> false
            | Some flags -> Util.IsFlagSet flags CommandFlags.HandlesEscape
        member x.IsWaitingForMoreInput = Option.isSome _runBindData
        member x.KeyRemapMode = 
            match _runBindData with
            | None -> None
            | Some bindData -> bindData.KeyRemapMode
        member x.Add command = x.Add command
        member x.Remove name = x.Remove name
        member x.ResetState () = x.ResetState()
        member x.Run ki = x.Run ki
        [<CLIEvent>]
        member x.CommandRan = _commandRanEvent.Publish








