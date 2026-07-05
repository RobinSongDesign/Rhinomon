using System;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;

namespace Rhinomon
{
    /// <summary>
    /// Subscribes to command / document / view events and turns them into
    /// (a) an idle clock for the pet's idle ladder and
    /// (b) O(1), allocation-free reaction counters that are folded into a single
    ///     pet reaction when a command ends (PRD F4), with a global 8 s cooldown.
    /// All handlers run on the UI thread and only touch integer/enum fields.
    /// </summary>
    internal sealed class ActivityMonitor
    {
        private const long ReactionCooldownMs = 8_000;
        private const int MassDeleteThreshold = 10;

        public PetEngine Engine;
        public PerfGovernor Governor;

        private bool _started;

        private int _commandDepth;
        private long _lastActivityMs = System.Environment.TickCount64;
        private long _lastReactionMs = -ReactionCooldownMs;

        // Per-command reaction counters, reset when a top-level command begins.
        private int _addCount;
        private int _deleteCount;
        private int _undoCount;

        // World-anchor bookkeeping for PerchScanner/PetEngine.
        private Guid _watchedObjectId = Guid.Empty;
        private bool _anchorDeleted;

        // Consecutive-failure counters, one per callback site (see Guard).
        private int _failBegin, _failEnd, _failUndo, _failAdd, _failDelete,
                    _failReplace, _failViewMod, _failViewActive, _failCloseDoc;

        public bool CommandRunning => _commandDepth > 0;

        /// <summary>Changes whenever any user activity is seen; used by the
        /// engine to detect the start of a new idle episode.</summary>
        public long LastActivityStamp => _lastActivityMs;

        public long IdleMilliseconds =>
            CommandRunning ? 0 : System.Environment.TickCount64 - _lastActivityMs;

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            Command.BeginCommand += OnBeginCommand;
            Command.EndCommand += OnEndCommand;
            Command.UndoRedo += OnUndoRedo;
            RhinoDoc.AddRhinoObject += OnAddObject;
            RhinoDoc.DeleteRhinoObject += OnDeleteObject;
            RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
            RhinoDoc.CloseDocument += OnCloseDocument;
            RhinoView.Modified += OnViewModified;
            RhinoView.SetActive += OnViewSetActive;
        }

        public void Stop()
        {
            if (!_started)
                return;
            _started = false;
            Command.BeginCommand -= OnBeginCommand;
            Command.EndCommand -= OnEndCommand;
            Command.UndoRedo -= OnUndoRedo;
            RhinoDoc.AddRhinoObject -= OnAddObject;
            RhinoDoc.DeleteRhinoObject -= OnDeleteObject;
            RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
            RhinoDoc.CloseDocument -= OnCloseDocument;
            RhinoView.Modified -= OnViewModified;
            RhinoView.SetActive -= OnViewSetActive;
        }

        public void TouchActivity()
        {
            _lastActivityMs = System.Environment.TickCount64;
        }

        public void WatchObject(Guid id)
        {
            _watchedObjectId = id;
            _anchorDeleted = false;
        }

        public void ClearWatch()
        {
            _watchedObjectId = Guid.Empty;
            _anchorDeleted = false;
        }

        public bool ConsumeAnchorDeleted()
        {
            if (!_anchorDeleted)
                return false;
            _anchorDeleted = false;
            return true;
        }

        // ---- event handlers (O(1), zero heap allocation) ---------------------

        private void OnBeginCommand(object sender, CommandEventArgs e)
        {
            try
            {
                _commandDepth++;
                if (_commandDepth == 1)
                {
                    _addCount = 0;
                    _deleteCount = 0;
                    _undoCount = 0;
                    Governor?.SetCommandRunning(true);
                    Engine?.OnStrongInterrupt();
                }
                TouchActivity();
                _failBegin = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failBegin, "ActivityMonitor.OnBeginCommand", ex);
            }
        }

        private void OnEndCommand(object sender, CommandEventArgs e)
        {
            try
            {
                if (_commandDepth > 0)
                    _commandDepth--;
                TouchActivity();
                if (_commandDepth == 0)
                {
                    Governor?.SetCommandRunning(false);
                    EvaluateReaction();
                }
                _failEnd = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failEnd, "ActivityMonitor.OnEndCommand", ex);
            }
        }

        private void OnUndoRedo(object sender, UndoRedoEventArgs e)
        {
            try
            {
                if (e.IsEndUndo || e.IsEndRedo)
                {
                    _undoCount++;
                    TouchActivity();
                    // Undo driven from outside a command (API/macros): react now,
                    // since no EndCommand will fold the counters for us.
                    if (_commandDepth == 0)
                        EvaluateReaction();
                }
                _failUndo = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failUndo, "ActivityMonitor.OnUndoRedo", ex);
            }
        }

        private void OnAddObject(object sender, RhinoObjectEventArgs e)
        {
            try
            {
                _addCount++;
                TouchActivity();
                if (_commandDepth == 0)
                    Engine?.OnStrongInterrupt(); // scripted/API document edit
                _failAdd = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failAdd, "ActivityMonitor.OnAddObject", ex);
            }
        }

        private void OnDeleteObject(object sender, RhinoObjectEventArgs e)
        {
            try
            {
                _deleteCount++;
                if (e.ObjectId == _watchedObjectId && _watchedObjectId != Guid.Empty)
                    _anchorDeleted = true;
                TouchActivity();
                if (_commandDepth == 0)
                    Engine?.OnStrongInterrupt();
                _failDelete = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failDelete, "ActivityMonitor.OnDeleteObject", ex);
            }
        }

        private void OnReplaceObject(object sender, RhinoReplaceObjectEventArgs e)
        {
            try
            {
                // A replace also raises delete+add for the same object; counting
                // it here would double-book, so only the idle clock is touched.
                TouchActivity();
                _failReplace = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failReplace, "ActivityMonitor.OnReplaceObject", ex);
            }
        }

        private void OnCloseDocument(object sender, DocumentEventArgs e)
        {
            try
            {
                // Any world anchor is meaningless in the next document.
                Engine?.ResetToHome();
                TouchActivity();
                _failCloseDoc = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failCloseDoc, "ActivityMonitor.OnCloseDocument", ex);
            }
        }

        private void OnViewModified(object sender, ViewEventArgs e)
        {
            try
            {
                TouchActivity();
                _failViewMod = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failViewMod, "ActivityMonitor.OnViewModified", ex);
            }
        }

        private void OnViewSetActive(object sender, ViewEventArgs e)
        {
            try
            {
                var view = e.View;
                if (view != null)
                {
                    PetSystem.SetActiveViewportId(view.ActiveViewportID);
                    Engine?.OnViewportChanged(); // the pet moves house
                }
                TouchActivity();
                _failViewActive = 0;
            }
            catch (Exception ex)
            {
                Guard.Fail(ref _failViewActive, "ActivityMonitor.OnViewSetActive", ex);
            }
        }

        /// <summary>
        /// Folds the counters of the finished command into at most one reaction.
        /// Triggers inside the global cooldown are dropped, not queued (PRD F4).
        /// </summary>
        private void EvaluateReaction()
        {
            int adds = _addCount;
            int deletes = _deleteCount;
            int undos = _undoCount;
            _addCount = 0;
            _deleteCount = 0;
            _undoCount = 0;

            if (Engine == null || (adds == 0 && deletes == 0 && undos == 0))
                return;

            long now = System.Environment.TickCount64;
            if (now - _lastReactionMs < ReactionCooldownMs)
                return;

            // Undo first: undoing a delete re-adds objects, which must not read
            // as "user created something new".
            if (undos > 0)
                Engine.React(PetReaction.Undo);
            else if (deletes >= MassDeleteThreshold)
                Engine.React(PetReaction.MassDelete);
            else if (adds > 0)
                Engine.React(PetReaction.NewObjects);
            else
                return;

            _lastReactionMs = now;
            // The command is over, so a single redraw to show the reaction does
            // not violate P6 (RedrawActiveView re-checks CommandRunning anyway).
            PetSystem.RedrawActiveView();
        }
    }
}
