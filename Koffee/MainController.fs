﻿namespace Koffee

open FSharp.Desktop.UI
open System.Text.RegularExpressions
open System.Threading.Tasks
open Utility
open ModelExtensions
open Koffee.ConfigExt

module MainHandler =
    let initModel (config: Config) openUserPath commandLinePath (model: MainModel) =
        config.Changed.Add (fun _ ->
            model.PathFormat <- config.PathFormat
            model.ShowFullPathInTitle <- config.Window.ShowFullPathInTitle)
        config.Load()
        let startupPath =
            commandLinePath |> Option.coalesce (
                match config.StartupPath with
                | RestorePrevious -> config.PreviousPath
                | DefaultPath -> config.DefaultPath)
        model.Path <- config.DefaultPath |> Path.Parse |> Option.coalesce Path.Root
        openUserPath (startupPath) model

    // path navigation

    let openPath getNodes path select (model: MainModel) =
        try
            let sortField, sortDesc = model.Sort
            let sorter = SortField.SortByTypeThen sortField sortDesc
            let nodes = getNodes path |> sorter
            if path <> model.Path then
                model.BackStack <- (model.Path, model.Cursor) :: model.BackStack
                model.ForwardStack <- []
                model.Path <- path
                model.Cursor <- 0
            let keepCursor = model.Cursor
            model.Nodes <- nodes
            model.SetCursor (
                match select with
                | SelectIndex index -> index
                | SelectName name ->
                    List.tryFindIndex (fun n -> n.Name = name) nodes
                    |> Option.coalesce model.Cursor
                | KeepSelect -> keepCursor)
            model.Status <- None
        with ex -> model.Status <- Some <| StatusType.fromExn "open path" ex

    let openUserPath getNode openPath pathStr (model: MainModel) =
        match Path.Parse pathStr with
        | Some path ->
            match getNode path with
            | Some node when node.Type = File ->
                openPath path.Parent (SelectName node.Name) model
            | _ ->
                openPath path KeepSelect model
        | None -> model.Status <- Some <| MainStatus.invalidPath pathStr

    let openSelected openPath openFile (model: MainModel) =
        let path = model.SelectedNode.Path
        match model.SelectedNode.Type with
        | Folder | Drive | Error ->
            openPath path KeepSelect model
        | File ->
            try
                openFile path
                model.Status <- Some <| MainStatus.openFile model.SelectedNode.Name
            with ex ->
                model.Status <- Some <| MainStatus.couldNotOpenFile path.Name ex.Message
        | Empty -> ()

    let back openPath (model: MainModel) =
        match model.BackStack with
        | (path, cursor) :: backTail ->
            let newForwardStack = (model.Path, model.Cursor) :: model.ForwardStack
            openPath path (SelectIndex cursor) model
            model.BackStack <- backTail
            model.ForwardStack <- newForwardStack
        | [] -> ()

    let forward openPath (model: MainModel) =
        match model.ForwardStack with
        | (path, cursor) :: forwardTail ->
            openPath path (SelectIndex cursor) model
            model.ForwardStack <- forwardTail
        | [] -> ()

    let sortList refresh field (model: MainModel) =
        let desc =
            match model.Sort with
            | f, desc when f = field -> not desc
            | _ -> field = Modified
        model.Sort <- field, desc
        refresh model
        model.Status <- Some <| MainStatus.sort field desc

    // input commands

    let private setCommandSelection cursorPos (model: MainModel) =
        cursorPos |> Option.iter (fun pos ->
            let fullName = model.CommandText
            let (name, ext) = Path.SplitName fullName
            model.CommandTextSelection <-
                match pos with
                | Begin -> (0, 0)
                | EndName -> (name.Length, 0)
                | End -> (fullName.Length, 0)
                | ReplaceName -> (0, name.Length)
                | ReplaceAll -> (0, fullName.Length)
        )

    let startInput (inputMode: CommandInput) (model: MainModel) =
        if inputMode.AllowedOnNodeType model.SelectedNode.Type then
            let text, pos =
                match inputMode with
                    | Rename pos -> model.SelectedNode.Name, (Some pos)
                    | _ -> "", None
            model.CommandInputMode <- Some inputMode
            model.CommandText <- text
            setCommandSelection pos model

    // cursor movement

    let private moveCursorToNext predicate reverse (model: MainModel) =
        let indexed = model.Nodes |> List.mapi (fun i n -> (i, n))
        let items =
            if reverse then
                Seq.append indexed.[model.Cursor..] indexed.[0..(model.Cursor-1)]
                |> Seq.rev
            else
                Seq.append indexed.[(model.Cursor+1)..] indexed.[0..model.Cursor]
        items
        |> Seq.choose (fun (i, n) -> if predicate n then Some i else None)
        |> Seq.tryHead
        |> Option.iter (fun index -> model.SetCursor index)

    let find char (model: MainModel) =
        model.LastFind <- Some char
        model.Status <- Some <| MainStatus.find char
        moveCursorToNext (fun n -> n.Name.[0] = char) false model

    let findNext (model: MainModel) =
        model.LastFind |> Option.iter (fun c -> find c model)

    let search searchStr reverse (model: MainModel) =
        let search = if searchStr <> "" then Some searchStr else None

        // if search is different or empty (to clear results), update node flags
        if search.IsNone || not <| MainStatus.isSearchStatus searchStr model.Status then
            let cursor = model.Cursor
            model.Nodes <- model.Nodes |> List.map (fun n ->
                let isMatch = search |> Option.exists (fun s ->
                    Regex.IsMatch(n.Name, s, RegexOptions.IgnoreCase))
                if isMatch && not n.IsSearchMatch then { n with IsSearchMatch = true }
                else if not isMatch && n.IsSearchMatch then { n with IsSearchMatch = false }
                else n)
            model.Cursor <- cursor
            let matches = model.Nodes |> Seq.filter (fun n -> n.IsSearchMatch) |> Seq.length
            model.Status <- search |> Option.map (MainStatus.search matches)

        if search.IsSome then
            model.LastSearch <- search

        moveCursorToNext (fun n -> n.IsSearchMatch) reverse model

    let searchNext reverse (model: MainModel) =
        model.LastSearch |> Option.iter (fun s -> search s reverse model)
 

    // item actions

    let runAsync (f: unit -> 'a) = f |> Task.Run |> Async.AwaitTask

    let performedAction action (model: MainModel) =
        model.UndoStack <- action :: model.UndoStack
        model.RedoStack <- []
        model.Status <- Some <| MainStatus.actionComplete action model.PathFormat

    let create fsCreate openPath nodeType name (model: MainModel) =
        try
            fsCreate nodeType (model.Path.Join name)
            openPath model.Path (SelectName name) model
            model |> performedAction (CreatedItem model.SelectedNode)
        with ex ->
            let action = CreatedItem { Path = model.Path; Name = name; Type = nodeType;
                                       Modified = None; Size = None; IsHidden = false; IsSearchMatch = false }
            model |> MainStatus.setActionExceptionStatus action ex

    let undoCreate isEmpty delete refresh node (model: MainModel) = async {
        try
            if isEmpty node.Path then
                model.Status <- Some <| MainStatus.undoingCreate node
                do! runAsync (fun () -> delete node.Path)
                if model.Path = node.Path.Parent then
                    refresh model
            else
                model.Status <- Some <| MainStatus.cannotUndoNonEmptyCreated node
        with ex ->
            let action = DeletedItem (node, true)
            model |> MainStatus.setActionExceptionStatus action ex
    }

    let rename fsMove openPath node newName (model: MainModel) =
        let action = RenamedItem (node, newName)
        try
            let newPath = node.Path.Parent.Join newName
            fsMove node.Path newPath
            openPath model.Path (SelectName newName) model
            model |> performedAction action
        with ex -> model |> MainStatus.setActionExceptionStatus action ex

    let undoRename move openPath oldNode curName (model: MainModel) =
        let parentPath = oldNode.Path.Parent
        let node = { oldNode with Name = curName; Path = parentPath.Join curName}
        try
            move node.Path oldNode.Path
            openPath parentPath (SelectName oldNode.Name) model
        with ex ->
            let action = RenamedItem (node, oldNode.Name)
            model |> MainStatus.setActionExceptionStatus action ex

    let bufferItem action (model: MainModel) =
        match model.SelectedNode.Type with
        | File | Folder ->
            model.ItemBuffer <- Some (model.SelectedNode, action)
            model.Status <- None
        | _ -> ()

    let getCopyName name i =
        let (nameNoExt, ext) = Path.SplitName name
        let number = if i = 0 then "" else sprintf " %i" (i+1)
        sprintf "%s (copy%s)%s" nameNoExt number ext

    let put (config: Config) exists getNode move copy openPath overwrite (model: MainModel) = async {
        match model.ItemBuffer with
        | Some (node, bufferAction) ->
            let sameFolder = node.Path.Parent = model.Path
            if bufferAction = Move && sameFolder then
                model.Status <- Some MainStatus.cannotMoveToSameFolder
            else
                let newName =
                    if bufferAction = Copy && sameFolder then
                        Seq.init 99 (getCopyName node.Name)
                        |> Seq.pick
                            (fun name ->
                                let path = model.Path.Join name
                                if not (exists path) then Some name else None)
                    else
                        node.Name
                let newPath = model.Path.Join newName
                match getNode newPath with
                | Some existing when not overwrite ->
                    // refresh node list to make sure we can see the existing file
                    // if the existing node is hidden, temporarily override ShowHidden
                    let overrideShowHidden = existing.IsHidden && not config.ShowHidden
                    if overrideShowHidden then config.ShowHidden <- true
                    openPath model.Path (SelectName existing.Name) model
                    if overrideShowHidden then config.ShowHidden <- false
                    startInput (Confirm Overwrite) model
                | _ ->
                    let fileSysAction, action =
                        match bufferAction with
                        | Move -> (move, MovedItem (node, newPath))
                        | Copy -> (copy, CopiedItem (node, newPath))
                    try
                        model.Status <- MainStatus.runningAction action model.PathFormat
                        do! runAsync (fun () -> fileSysAction node.Path newPath)
                        openPath model.Path (SelectName newName) model
                        model.ItemBuffer <- None
                        model |> performedAction action
                    with ex -> model |> MainStatus.setActionExceptionStatus action ex
        | None -> ()
    }

    let undoMove exists move openPath node newPath (model: MainModel) = async {
        if exists newPath then
            // todo: prompt for overwrite here
            model.Status <- Some <| ErrorMessage (sprintf "Cannot undo move of %s because an item exists in its previous location" node.Name)
        else
            try
                model.Status <- Some <| MainStatus.undoingMove node
                do! runAsync (fun () -> move newPath node.Path)
                openPath node.Path.Parent (SelectName node.Name) model
            with ex ->
                let action = MovedItem ({ node with Path = newPath }, node.Path)
                model |> MainStatus.setActionExceptionStatus action ex
    }

    let undoCopy getNode fsDelete fsRecycle refresh node (newPath: Path) (model: MainModel) = async {
        let mutable isDeletionPermanent = false
        try
            let copyModified = getNode newPath |> Option.bind (fun n -> n.Modified)
            isDeletionPermanent <-
                match node.Modified, copyModified with
                | Some orig, Some copy when orig = copy -> true
                | _ -> false
            let fileSysFunc = if isDeletionPermanent then fsDelete else fsRecycle
            model.Status <- Some <| MainStatus.undoingCopy node isDeletionPermanent
            do! runAsync (fun () -> fileSysFunc newPath)
            if model.Path = newPath.Parent then
                refresh model
        with ex ->
            let action = DeletedItem ({ node with Path = newPath }, isDeletionPermanent)
            model |> MainStatus.setActionExceptionStatus action ex
    }

    let recycle isRecyclable delete (model: MainModel) = async {
        let node = model.SelectedNode
        model.Status <- Some <| MainStatus.checkingIsRecyclable
        let! canRecycle = runAsync (fun () -> isRecyclable node.Path)
        if canRecycle then
            do! delete node false model
        else
            model.Status <- Some <| MainStatus.cannotRecycle node
    }

    let delete fsDelete fsRecycle refresh node permanent (model: MainModel) = async {
        let action = DeletedItem (node, permanent)
        try
            let fileSysFunc = if permanent then fsDelete else fsRecycle
            model.Status <- MainStatus.runningAction action model.PathFormat
            do! runAsync (fun () -> fileSysFunc node.Path)
            refresh model
            model |> performedAction action
        with ex -> model |> MainStatus.setActionExceptionStatus action ex
    }


type MainController(fileSys: IFileSystemService,
                    settingsFactory: unit -> Mvc<SettingsEvents, SettingsModel>,
                    config: Config,
                    commandLinePath: string option) =
    let runAsync (f: unit -> 'a) = f |> Task.Run |> Async.AwaitTask
    let mutable taskRunning = false

    interface IController<MainEvents, MainModel> with
        member this.InitModel model = MainHandler.initModel config this.OpenUserPath commandLinePath model
        member this.Dispatcher = this.LockingDispatcher

    member this.LockingDispatcher evt : EventHandler<MainModel> =
        match this.Dispatcher evt with
        | Sync handler -> Sync (fun m -> if not taskRunning then handler m)
        | Async handler ->
            Async (fun m -> async {
                if not taskRunning then
                    taskRunning <- true
                    do! handler m
                    taskRunning <- false
            })

    member this.Dispatcher = function
        | CursorUp -> Sync (fun m -> m.SetCursor (m.Cursor - 1))
        | CursorUpHalfPage -> Sync (fun m -> m.SetCursor (m.Cursor - m.HalfPageScroll))
        | CursorDown -> Sync (fun m -> m.SetCursor (m.Cursor + 1))
        | CursorDownHalfPage -> Sync (fun m -> m.SetCursor (m.Cursor + m.HalfPageScroll))
        | CursorToFirst -> Sync (fun m -> m.SetCursor 0)
        | CursorToLast -> Sync (fun m -> m.SetCursor (m.Nodes.Length - 1))
        | OpenPath p -> Sync (this.OpenUserPath p)
        | OpenSelected -> Sync (MainHandler.openSelected this.OpenPath fileSys.OpenFile)
        | OpenParent -> Sync (fun m -> this.OpenPath m.Path.Parent (SelectName m.Path.Name) m)
        | Back -> Sync (MainHandler.back this.OpenPath)
        | Forward -> Sync (MainHandler.forward this.OpenPath)
        | Refresh -> Sync this.Refresh
        | Undo -> Async this.Undo
        | Redo -> Async this.Redo
        | StartInput inputMode -> Sync (MainHandler.startInput inputMode)
        | ExecuteCommand -> Sync this.ExecuteCommand
        | CommandCharTyped c -> Async (this.CommandCharTyped c)
        | FindNext -> Sync MainHandler.findNext
        | SearchNext -> Sync (MainHandler.searchNext false)
        | SearchPrevious -> Sync (MainHandler.searchNext true)
        | StartMove -> Sync (MainHandler.bufferItem Move)
        | StartCopy -> Sync (MainHandler.bufferItem Copy)
        | Put -> Async (this.Put false)
        | Recycle -> Async this.Recycle
        | PromptDelete -> Sync (MainHandler.startInput (Confirm Delete))
        | SortList field -> Sync (MainHandler.sortList this.Refresh field)
        | ToggleHidden -> Sync this.ToggleHidden
        | OpenSettings -> Sync this.OpenSettings
        | OpenExplorer -> Sync this.OpenExplorer
        | OpenCommandLine -> Sync this.OpenCommandLine
        | OpenWithTextEditor -> Sync this.OpenWithTextEditor
        | Exit -> Sync ignore // handled by view

    member this.OpenPath = MainHandler.openPath (fileSys.GetNodes config.ShowHidden)
    member this.OpenUserPath = MainHandler.openUserPath fileSys.GetNode this.OpenPath
    member this.Refresh model = this.OpenPath model.Path KeepSelect model

    member this.ToggleHidden model =
        config.ShowHidden <- not config.ShowHidden
        config.Save()
        this.OpenPath model.Path (SelectName model.SelectedNode.Name) model
        model.Status <- Some <| MainStatus.toggleHidden config.ShowHidden

    member this.ExecuteCommand model =
        let mode = model.CommandInputMode
        model.CommandInputMode <- None
        match mode with
        | Some Search -> MainHandler.search model.CommandText false model
        | Some CreateFile -> this.Create File model.CommandText model
        | Some CreateFolder -> this.Create Folder model.CommandText model
        | Some (Rename _) -> this.Rename model.SelectedNode model.CommandText model
        | _ -> ()

    member this.CommandCharTyped char model = async {
        match model.CommandInputMode with
        | Some Find ->
            MainHandler.find char model
            model.CommandInputMode <- None
        | Some GoToBookmark ->
            model.CommandInputMode <- None
            match config.GetBookmark char with
            | Some path -> this.OpenUserPath path model
            | None -> model.Status <- Some <| MainStatus.noBookmark char
        | Some SetBookmark ->
            model.CommandInputMode <- None
            config.SetBookmark char (model.Path.Format Windows)
            config.Save()
            model.Status <- Some <| MainStatus.setBookmark char model.PathFormatted
        | Some DeleteBookmark ->
            model.CommandInputMode <- None
            match config.GetBookmark char with
            | Some path ->
                config.RemoveBookmark char
                config.Save()
                model.Status <- Some <| MainStatus.deletedBookmark char path
            | None -> model.Status <- Some <| MainStatus.noBookmark char
        | Some (Confirm confirmType) ->
            match char with
            | 'y' -> 
                model.CommandInputMode <- None
                match confirmType with
                | Overwrite -> do! this.Put true model
                | Delete -> do! this.Delete model.SelectedNode true model
            | 'n' ->
                model.CommandInputMode <- None
                match confirmType with
                | Overwrite when not config.ShowHidden && model.Nodes |> Seq.exists (fun n -> n.IsHidden) ->
                    // if we were temporarily showing hidden files, refresh
                    this.Refresh model
                | _ -> ()
                model.Status <- Some <| MainStatus.cancelled
            | _ ->
                model.CommandText <- ""
        | _ -> ()
    }

    member this.Create = MainHandler.create fileSys.Create this.OpenPath
    member this.Rename = MainHandler.rename fileSys.Move this.OpenPath
    member this.Put = MainHandler.put config fileSys.Exists fileSys.GetNode fileSys.Move fileSys.Copy this.OpenPath
    member this.Recycle = MainHandler.recycle fileSys.IsRecyclable this.Delete
    member this.Delete = MainHandler.delete fileSys.Delete fileSys.Recycle this.Refresh

    member this.Undo model = async {
        match model.UndoStack with
        | action :: rest ->
            match action with
            | CreatedItem node ->
                do! MainHandler.undoCreate fileSys.IsEmpty fileSys.Delete this.Refresh node model
            | RenamedItem (oldNode, curName) ->
                MainHandler.undoRename fileSys.Move this.OpenPath oldNode curName model
            | MovedItem (node, newPath) ->
                do! MainHandler.undoMove fileSys.Exists fileSys.Move this.OpenPath node newPath model
            | CopiedItem (node, newPath) ->
                do! MainHandler.undoCopy fileSys.GetNode fileSys.Delete fileSys.Recycle this.Refresh node newPath model
            | DeletedItem (node, permanent) ->
                model.Status <- Some <| MainStatus.cannotUndoDelete permanent node

            model.UndoStack <- rest
            if not model.HasErrorStatus then
                model.RedoStack <- action :: model.RedoStack
                model.Status <- Some <| MainStatus.undoAction action model.PathFormat
        | [] -> model.Status <- Some <| MainStatus.noUndoActions
    }

    member this.Redo model = async {
        match model.RedoStack with
        | action :: rest ->
            let goToPath (nodePath: Path) =
                let path = nodePath.Parent
                if path <> model.Path then
                    this.OpenPath path KeepSelect model
            match action with
            | CreatedItem node ->
                goToPath node.Path
                this.Create node.Type node.Name model
            | RenamedItem (node, newName) ->
                goToPath node.Path
                this.Rename node newName model
            | MovedItem (node, newPath)
            | CopiedItem (node, newPath) ->
                let moveOrCopy =
                    match action with
                    | MovedItem _ -> Move
                    | _ -> Copy
                goToPath newPath
                let buffer = model.ItemBuffer
                model.ItemBuffer <- Some (node, moveOrCopy)
                model.Status <- MainStatus.redoingAction action model.PathFormat
                do! this.Put false model
                model.ItemBuffer <- buffer
            | DeletedItem (node, permanent) ->
                goToPath node.Path
                model.Status <- MainStatus.redoingAction action model.PathFormat
                do! this.Delete node permanent model

            model.RedoStack <- rest
            model.Status <- Some <| MainStatus.redoAction action model.PathFormat
        | [] -> model.Status <- Some <| MainStatus.noRedoActions
    }

    member this.OpenExplorer model =
        fileSys.OpenExplorer model.SelectedNode
        model.Status <- Some <| MainStatus.openExplorer

    member this.OpenCommandLine model =
        if model.Path <> Path.Root then
            fileSys.OpenCommandLine model.Path
            model.Status <- Some <| MainStatus.openCommandLine model.PathFormatted

    member this.OpenWithTextEditor model =
        match model.SelectedNode.Type with
        | File ->
            try
                fileSys.OpenWith config.TextEditor model.SelectedNode.Path
                model.Status <- Some <| MainStatus.openTextEditor model.SelectedNode.Name
            with ex ->
                model.Status <- Some <| MainStatus.couldNotOpenTextEditor ex.Message
        | _ -> ()

    member this.OpenSettings model =
        let settings = settingsFactory()
        settings.StartDialog() |> ignore

        model.PathFormat <- config.PathFormat
        model.ShowFullPathInTitle <- config.Window.ShowFullPathInTitle
        this.Refresh model
