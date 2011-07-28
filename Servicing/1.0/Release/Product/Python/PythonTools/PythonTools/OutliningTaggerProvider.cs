﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Parsing.Ast;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.PythonTools {
    [Export(typeof(ITaggerProvider)), ContentType(PythonCoreConstants.ContentType)]
    [TagType(typeof(IOutliningRegionTag))]
    class OutliningTaggerProvider : ITaggerProvider {
        #region ITaggerProvider Members

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag {
            return (ITagger<T>)(buffer.GetOutliningTagger() ?? new OutliningTagger(buffer));
        }

        #endregion

        internal class OutliningTagger : ITagger<IOutliningRegionTag> {
            private readonly ITextBuffer _buffer;
            private readonly Timer _timer;
            private bool _enabled, _eventHooked;

            public OutliningTagger(ITextBuffer buffer) {
                _buffer = buffer;
                _buffer.Properties[typeof(OutliningTagger)] = this;
                if (PythonToolsPackage.Instance != null) {
                    _enabled = PythonToolsPackage.Instance.AdvancedEditorOptionsPage.EnterOutliningModeOnOpen;
                }
                _timer = new Timer(TagUpdate, null, Timeout.Infinite, Timeout.Infinite);                
            }

            public bool Enabled {
                get {
                    return _enabled;
                }
            }

            public void Enable() {
                _enabled = true;
                var snapshot = _buffer.CurrentSnapshot;
                var tagsChanged = TagsChanged;
                if (tagsChanged != null) {
                    tagsChanged(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, new Span(0, snapshot.Length))));
                }
            }

            public void Disable() {
                _enabled = false;
                var snapshot = _buffer.CurrentSnapshot;
                var tagsChanged = TagsChanged;
                if (tagsChanged != null) {
                    tagsChanged(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, new Span(0, snapshot.Length))));
                }
            }

            #region ITagger<IOutliningRegionTag> Members

            public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans) {
                IPythonProjectEntry classifier;
                if (_enabled && _buffer.TryGetPythonAnalysis(out classifier)) {
                    if (!_eventHooked) {
                        classifier.OnNewParseTree += OnNewParseTree;
                        _eventHooked = true;
                    }
                    PythonAst ast;
                    IAnalysisCookie cookie;
                    classifier.GetTreeAndCookie(out ast, out cookie);
                    SnapshotCookie snapCookie = cookie as SnapshotCookie;

                    if (ast != null && 
                        snapCookie != null && 
                        snapCookie.Snapshot.TextBuffer == spans[0].Snapshot.TextBuffer) {   // buffer could have changed if file was closed and re-opened
                        return ProcessSuite(spans, ast, ast.Body as SuiteStatement, snapCookie.Snapshot, true);
                    }
                }

                return new ITagSpan<IOutliningRegionTag>[0];
            }

            private void OnNewParseTree(object sender, EventArgs e) {
                IPythonProjectEntry classifier;
                if (_buffer.TryGetPythonAnalysis(out classifier)) {                    
                    _timer.Change(300, Timeout.Infinite);
                }
            }

            private void TagUpdate(object unused) {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                var snapshot = _buffer.CurrentSnapshot;
                var tagsChanged = TagsChanged;
                if (tagsChanged != null) {
                    tagsChanged(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, new Span(0, snapshot.Length))));
                }
            }

            private IEnumerable<ITagSpan<IOutliningRegionTag>> ProcessSuite(NormalizedSnapshotSpanCollection spans, PythonAst ast, SuiteStatement suite, ITextSnapshot snapshot, bool isTopLevel) {
                if (suite != null) {
                    // TODO: Binary search the statements?  The perf of this seems fine for the time being
                    // w/ a 5000+ line file though.                    
                    foreach (var statement in suite.Statements) {
                        SnapshotSpan? span = ShouldInclude(statement, spans);
                        if (span == null) {
                            continue;
                        }

                        FunctionDefinition funcDef = statement as FunctionDefinition;
                        if (funcDef != null) {
                            TagSpan tagSpan = GetFunctionSpan(ast, snapshot, funcDef);

                            if (tagSpan != null) {
                                yield return tagSpan;
                            }
                        }

                        ClassDefinition classDef = statement as ClassDefinition;
                        if (classDef != null) {
                            TagSpan tagSpan = GetClassSpan(ast, snapshot, classDef);

                            if (tagSpan != null) {
                                yield return tagSpan;
                            }

                            // recurse into the class definition and outline it's members
                            foreach (var v in ProcessSuite(spans, ast, classDef.Body as SuiteStatement, snapshot, false)) {
                                yield return v;
                            }
                        }

                        if (isTopLevel) {
                            IfStatement ifStmt = statement as IfStatement;
                            if (ifStmt != null) {
                                TagSpan tagSpan = GetIfSpan(ast, snapshot, ifStmt);

                                if (tagSpan != null) {
                                    yield return tagSpan;
                                }
                            }

                            WhileStatement whileStatment = statement as WhileStatement;
                            if (whileStatment != null) {
                                TagSpan tagSpan = GetWhileSpan(ast, snapshot, whileStatment);

                                if (tagSpan != null) {
                                    yield return tagSpan;
                                }
                            }

                            ForStatement forStatement = statement as ForStatement;
                            if (forStatement != null) {
                                TagSpan tagSpan = GetForSpan(ast, snapshot, forStatement);

                                if (tagSpan != null) {
                                    yield return tagSpan;
                                }
                            }
                        }
                    }
                }
            }

            private static TagSpan GetForSpan(PythonAst ast, ITextSnapshot snapshot, ForStatement forStmt) {
                TagSpan tagSpan = null;
                try {
                    var start = forStmt.GetStart(ast);
                    var end = forStmt.GetEnd(ast);
                    var testLen = forStmt.List.EndIndex - forStmt.StartIndex + 1;
                    if (start.IsValid && end.IsValid) {
                        int length = end.Index - start.Index - testLen;
                        if (length > 0) {
                            var forSpan = GetFinalSpan(snapshot,
                                start.Index + testLen,
                                length
                            );

                            tagSpan = new TagSpan(
                                new SnapshotSpan(snapshot, forSpan),
                                new OutliningTag(snapshot, forSpan, false)
                            );
                        }
                    }
                } catch (ArgumentException) {
                    // sometimes Python's parser gives usbad spans, ignore those and fix the parser
                    Debug.Assert(false, "bad argument when making span/tag");
                }
                return tagSpan;
            }

            private static TagSpan GetWhileSpan(PythonAst ast, ITextSnapshot snapshot, WhileStatement whileStmt) {
                TagSpan tagSpan = null;
                try {
                    var start = whileStmt.GetStart(ast);
                    var end = whileStmt.GetEnd(ast);
                    var testLen = whileStmt.Test.EndIndex - whileStmt.StartIndex + 1;
                    if (start.IsValid && end.IsValid) {
                        int length = end.Index - start.Index - testLen;
                        if (length > 0) {
                            var whileSpan = GetFinalSpan(snapshot,
                                start.Index + testLen,
                                length
                            );

                            tagSpan = new TagSpan(
                                new SnapshotSpan(snapshot, whileSpan),
                                new OutliningTag(snapshot, whileSpan, false)
                            );
                        }
                    }
                } catch (ArgumentException) {
                    // sometimes Python's parser gives usbad spans, ignore those and fix the parser
                    Debug.Assert(false, "bad argument when making span/tag");
                }
                return tagSpan;
            }

            private static TagSpan GetIfSpan(PythonAst ast, ITextSnapshot snapshot, IfStatement ifStmt) {
                TagSpan tagSpan = null;
                try {
                    var ifStmtStart = ifStmt.GetStart(ast);
                    var ifStmtEnd = ifStmt.GetEnd(ast);
                    var testLen = ifStmt.Tests[0].HeaderIndex - ifStmt.StartIndex + 1;
                    if (ifStmtStart.IsValid && ifStmtEnd.IsValid) {
                        int length = ifStmtEnd.Index - ifStmtStart.Index - testLen;
                        if (length > 0) {
                            var ifSpan = GetFinalSpan(snapshot,
                                ifStmtStart.Index + testLen,
                                length
                            );

                            tagSpan = new TagSpan(
                                new SnapshotSpan(snapshot, ifSpan),
                                new OutliningTag(snapshot, ifSpan, false)
                            );
                        }
                    }
                } catch (ArgumentException) {
                    // sometimes Python's parser gives usbad spans, ignore those and fix the parser
                    Debug.Assert(false, "bad argument when making span/tag");
                }
                return tagSpan;
            }

            private static TagSpan GetFunctionSpan(PythonAst ast, ITextSnapshot snapshot, FunctionDefinition funcDef) {
                TagSpan tagSpan = null;
                try {
                    var funcDefStart = funcDef.GetStart(ast);
                    var funcDefEnd = funcDef.GetEnd(ast);
                    int nameLen = funcDef.HeaderIndex - funcDef.StartIndex + 1;
                    if (funcDefStart.IsValid && funcDefEnd.IsValid) {
                        int length = funcDefEnd.Index - funcDefStart.Index - nameLen;
                        if (length >= 0 && length < snapshot.Length - (funcDefStart.Index + nameLen)) {
                            var funcSpan = GetFinalSpan(snapshot,
                                funcDefStart.Index + nameLen,
                                length);

                            tagSpan = new TagSpan(
                                new SnapshotSpan(snapshot, funcSpan),
                                new OutliningTag(snapshot, funcSpan, true)
                            );
                        }
                    }
                } catch (ArgumentException) {
                    // sometimes Python's parser gives usbad spans, ignore those and fix the parser
                    Debug.Assert(false, "bad argument when making span/tag");
                }
                return tagSpan;
            }

            private static TagSpan GetClassSpan(PythonAst ast, ITextSnapshot snapshot, ClassDefinition classDef) {
                TagSpan tagSpan = null;
                try {
                    var classDefStart = classDef.GetStart(ast);
                    var classDefEnd = classDef.GetEnd(ast);
                    int nameLen = classDef.HeaderIndex - classDef.StartIndex + 1;
                    if (classDefStart.IsValid && classDefEnd.IsValid) {
                        var classSpan = GetFinalSpan(snapshot,
                            classDefStart.Index + nameLen,
                            classDefEnd.Index - classDefStart.Index - nameLen
                        );

                        tagSpan = new TagSpan(
                            new SnapshotSpan(snapshot, classSpan),
                            new OutliningTag(snapshot, classSpan, false)
                        );
                    }
                } catch (ArgumentException) {
                    // sometimes Python's parser gives usbad spans, ignore those and fix the parser
                    Debug.Assert(false, "bad argument when making span/tag");
                }
                return tagSpan;
            }

            private static Span GetFinalSpan(ITextSnapshot snapshot, int start, int length) {
                Debug.Assert(start + length <= snapshot.Length);
                int cnt = 0;
                var text = snapshot.GetText(start, length);

                // remove up to 2 \r\n's if we just end with these, this will leave a space between the methods
                while (length > 0 && ((Char.IsWhiteSpace(text[length - 1])) || ((text[length - 1] == '\r' || text[length - 1] == '\n') && cnt++ < 4))) {
                    length--;
                }
                return new Span(start, length);
            }

            private SnapshotSpan? ShouldInclude(Statement statement, NormalizedSnapshotSpanCollection spans) {
                if (spans.Count == 1 && spans[0].Length == spans[0].Snapshot.Length) {
                    // we're processing the entire snapshot
                    return spans[0];
                }

                for (int i = 0; i < spans.Count; i++) {
                    if (spans[i].IntersectsWith(Span.FromBounds(statement.StartIndex, statement.EndIndex))) {
                        return spans[i];
                    }
                }
                return null;
            }

            public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

            #endregion
        }

        class TagSpan : ITagSpan<IOutliningRegionTag> {
            private readonly SnapshotSpan _span;
            private readonly OutliningTag _tag;

            public TagSpan(SnapshotSpan span, OutliningTag tag) {
                _span = span;
                _tag = tag;
            }

            #region ITagSpan<IOutliningRegionTag> Members

            public SnapshotSpan Span {
                get { return _span; }
            }

            public IOutliningRegionTag Tag {
                get { return _tag; }
            }

            #endregion
        }

        class OutliningTag : IOutliningRegionTag {
            private readonly ITextSnapshot _snapshot;
            private readonly Span _span;
            private readonly bool _isImplementation;

            public OutliningTag(ITextSnapshot iTextSnapshot, Span span, bool isImplementation) {
                _snapshot = iTextSnapshot;
                _span = span;
                _isImplementation = isImplementation;
            }

            #region IOutliningRegionTag Members

            public object CollapsedForm {
                get { return "..."; }
            }

            public object CollapsedHintForm {
                get {
                    string collapsedHint = _snapshot.GetText(_span);

                    string[] lines = collapsedHint.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    // remove any leading white space for the preview
                    if (lines.Length > 0) {
                        int smallestWhiteSpace = Int32.MaxValue;
                        for (int i = 0; i < lines.Length; i++) {
                            string curLine = lines[i];

                            for (int j = 0; j < curLine.Length; j++) {
                                if (curLine[j] != ' ') {
                                    smallestWhiteSpace = Math.Min(j, smallestWhiteSpace);
                                }
                            }
                        }

                        for (int i = 0; i < lines.Length; i++) {
                            if (lines[i].Length >= smallestWhiteSpace) {
                                lines[i] = lines[i].Substring(smallestWhiteSpace);
                            }
                        }

                        return String.Join("\r\n", lines);
                    }
                    return collapsedHint;
                }
            }

            public bool IsDefaultCollapsed {
                get { return false; }
            }

            public bool IsImplementation {
                get { return _isImplementation; }
            }

            #endregion
        }
    }

    static class OutliningTaggerProviderExtensions {
        public static OutliningTaggerProvider.OutliningTagger GetOutliningTagger(this ITextView self) {
            return self.TextBuffer.GetOutliningTagger();
        }

        public static OutliningTaggerProvider.OutliningTagger GetOutliningTagger(this ITextBuffer self) {
            OutliningTaggerProvider.OutliningTagger res;
            if (self.Properties.TryGetProperty<OutliningTaggerProvider.OutliningTagger>(typeof(OutliningTaggerProvider.OutliningTagger), out res)) {
                return res;
            }
            return null;
        }
    }
}