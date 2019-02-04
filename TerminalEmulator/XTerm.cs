﻿// Copyright 2004-2017 The Poderosa Project.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Globalization;

using Poderosa.Document;
using Poderosa.ConnectionParam;
using Poderosa.View;
using Poderosa.Preferences;

namespace Poderosa.Terminal {
    internal class XTerm : AbstractTerminal {

        private class ControlCode {
            public const char NUL = '\u0000';
            public const char BEL = '\u0007';
            public const char BS = '\u0008';
            public const char HT = '\u0009';
            public const char LF = '\u000a';
            public const char VT = '\u000b';
            public const char CR = '\u000d';
            public const char SO = '\u000e';
            public const char SI = '\u000f';
            public const char ESC = '\u001b';
            public const char ST = '\u009c';
        }

        private StringBuilder _escapeSequence;
        private IModalCharacterTask _currentCharacterTask;

        protected bool _insertMode;
        protected bool _scrollRegionRelative;

        private bool _gotEscape;

        private bool _wrapAroundMode;
        private bool _reverseVideo;
        private bool[] _tabStops;

        private readonly ScreenBufferManager _screenBuffer;
        private readonly BracketedPasteModeManager _bracketedPasteMode;
        private readonly MouseTrackingManager _mouseTracking;
        private readonly FocusReportingManager _focusReporting;

        public XTerm(TerminalInitializeInfo info)
            : base(info) {
            _escapeSequence = new StringBuilder();
            _processCharResult = ProcessCharResult.Processed;

            _insertMode = false;
            _scrollRegionRelative = false;

            _wrapAroundMode = true;
            _tabStops = new bool[GetDocument().TerminalWidth];
            InitTabStops();

            _screenBuffer = new ScreenBufferManager(this);
            _bracketedPasteMode = new BracketedPasteModeManager(this);
            _mouseTracking = new MouseTrackingManager(this);
            _focusReporting = new FocusReportingManager(this);
        }

        protected override void ResetInternal() {
            _escapeSequence = new StringBuilder();
            _processCharResult = ProcessCharResult.Processed;
            _insertMode = false;
            _scrollRegionRelative = false;
        }

        internal override byte[] GetPasteLeadingBytes() {
            return _bracketedPasteMode.GetPasteLeadingBytes();
        }

        internal override byte[] GetPasteTrailingBytes() {
            return _bracketedPasteMode.GetPasteTrailingBytes();
        }

        public override void ProcessChar(char ch) {
            if (_gotEscape) {
                _gotEscape = false;
                if (ch == '\\') {
                    // ESC \ --> ST (9C)
                    // Note:
                    //  The conversion of "ESC ch" pair is applied
                    //  only for the "ESC \" case because it may be used
                    //  for terminating the escape sequence.
                    //  After this conversion, we can consider ESC as the start
                    //  of the new escape sequence.
                    ProcessChar2(ControlCode.ST);
                    return;
                }
                ProcessChar2(ControlCode.ESC);
            }

            if (ch == ControlCode.ESC) {
                _gotEscape = true;
            }
            else {
                ProcessChar2(ch);
            }
        }

        private void ProcessChar2(char ch) {
            if (_processCharResult != ProcessCharResult.Escaping) {
                if (ch == ControlCode.ESC) {
                    _processCharResult = ProcessCharResult.Escaping;
                }
                else {
                    if (_currentCharacterTask != null) { //マクロなど、charを取るタイプ
                        _currentCharacterTask.ProcessChar(ch);
                    }

                    this.LogService.XmlLogger.Write(ch);

                    if (Unicode.IsControlCharacter(ch))
                        _processCharResult = ProcessControlChar(ch);
                    else
                        _processCharResult = ProcessNormalChar(ch);
                }
            }
            else {
                if (ch == ControlCode.NUL)
                    return; //シーケンス中にNULL文字が入っているケースが確認された なお今はXmlLoggerにもこのデータは行かない。

                if (ch == ControlCode.ESC) {
                    // escape sequence restarted ?
                    // save log silently
                    RuntimeUtil.SilentReportException(new UnknownEscapeSequenceException("Incomplete escape sequence: ESC " + _escapeSequence.ToString()));
                    _escapeSequence.Remove(0, _escapeSequence.Length);
                    return;
                }

                _escapeSequence.Append(ch);
                bool end_flag = false; //escape sequenceの終わりかどうかを示すフラグ
                if (_escapeSequence.Length == 1) { //ESC+１文字である場合
                    end_flag = ('0' <= ch && ch <= '9') || ('a' <= ch && ch <= 'z') || ('A' <= ch && ch <= 'Z' && ch != 'P') || ch == '>' || ch == '=' || ch == '|' || ch == '}' || ch == '~';
                }
                else if (_escapeSequence[0] == ']') { //OSCの終端はBELかST(String Terminator)
                    end_flag = (ch == ControlCode.BEL) || (ch == ControlCode.ST);
                    // Note: The conversion from "ESC \" to ST would be done in XTerm.ProcessChar(char).
                }
                else if (this._escapeSequence[0] == '@') {
                    end_flag = (ch == '0') || (ch == '1');
                }
                else if (this._escapeSequence[0] == 'P') {  // DCS
                    end_flag = (ch == ControlCode.ST);
                }
                else {
                    end_flag = ('a' <= ch && ch <= 'z') || ('A' <= ch && ch <= 'Z') || ch == '@' || ch == '~' || ch == '|' || ch == '{';
                }

                if (end_flag) { //シーケンスのおわり
                    char[] seq = _escapeSequence.ToString().ToCharArray();

                    this.LogService.XmlLogger.EscapeSequence(seq);

                    try {
                        char code = seq[0];
                        _processCharResult = ProcessCharResult.Unsupported; //ProcessEscapeSequenceで例外が来た後で状態がEscapingはひどい結果を招くので
                        _processCharResult = ProcessEscapeSequence(code, seq, 1);
                        if (_processCharResult == ProcessCharResult.Unsupported)
                            throw new UnknownEscapeSequenceException("Unknown escape sequence: ESC " + new string(seq));
                    }
                    catch (UnknownEscapeSequenceException ex) {
                        CharDecodeError(GEnv.Strings.GetString("Message.EscapesequenceTerminal.UnsupportedSequence") + ex.Message);
                        RuntimeUtil.SilentReportException(ex);
                    }
                    finally {
                        _escapeSequence.Remove(0, _escapeSequence.Length);
                    }
                }
                else
                    _processCharResult = ProcessCharResult.Escaping;
            }
        }

        protected ProcessCharResult ProcessNormalChar(char ch) {
            UnicodeChar unicodeChar;
            if (!base.UnicodeCharConverter.Feed(ch, out unicodeChar)) {
                return ProcessCharResult.Processed;
            }

            return ProcessNormalUnicodeChar(unicodeChar);
        }

        public bool ReverseVideo {
            get {
                return _reverseVideo;
            }
        }

        public override bool ProcessMouse(TerminalMouseAction action, MouseButtons button, Keys modKeys, int row, int col) {

            return _mouseTracking.ProcessMouse(
                        action: action,
                        button: button,
                        modKeys: modKeys,
                        row: row,
                        col: col);
        }

        public override void OnGotFocus() {
            _focusReporting.OnGotFocus();
        }

        public override void OnLostFocus() {
            _focusReporting.OnLostFocus();
        }

        protected ProcessCharResult ProcessNormalUnicodeChar(UnicodeChar ch) {
            //WrapAroundがfalseで、キャレットが右端のときは何もしない
            if (!_wrapAroundMode && _manipulator.CaretColumn >= GetDocument().TerminalWidth - 1)
                return ProcessCharResult.Processed;

            if (_insertMode)
                _manipulator.InsertBlanks(_manipulator.CaretColumn, ch.IsWideWidth ? 2 : 1, _currentdecoration);

            //既に画面右端にキャレットがあるのに文字が来たら改行をする
            int tw = GetDocument().TerminalWidth;
            if (_manipulator.CaretColumn + (ch.IsWideWidth ? 2 : 1) > tw) {
                _manipulator.EOLType = EOLType.Continue;
                GLine lineUpdated = GetDocument().UpdateCurrentLine(_manipulator);
                if (lineUpdated != null) {
                    this.LogService.TextLogger.WriteLine(lineUpdated);
                }
                GetDocument().LineFeed();
                _manipulator.Load(GetDocument().CurrentLine, 0);
            }

            //画面のリサイズがあったときは、_manipulatorのバッファサイズが不足の可能性がある
            if (tw > _manipulator.BufferSize)
                _manipulator.ExpandBuffer(tw);

            //通常文字の処理
            _manipulator.PutChar(ch, _currentdecoration);

            return ProcessCharResult.Processed;
        }

        protected ProcessCharResult ProcessControlChar(char ch) {
            if (ch == ControlCode.LF || ch == ControlCode.VT) { //Vertical TabはLFと等しい
                LineFeedRule rule = GetTerminalSettings().LineFeedRule;
                if (rule == LineFeedRule.Normal) {
                    DoLineFeed();
                }
                else if (rule == LineFeedRule.LFOnly) {
                    DoCarriageReturn();
                    DoLineFeed();
                }
                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.CR) {
                LineFeedRule rule = GetTerminalSettings().LineFeedRule;
                if (rule == LineFeedRule.Normal) {
                    DoCarriageReturn();
                }
                else if (rule == LineFeedRule.CROnly) {
                    DoCarriageReturn();
                    DoLineFeed();
                }
                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.BEL) {
                this.IndicateBell();
                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.BS) {
                //行頭で、直前行の末尾が継続であった場合行を戻す
                if (_manipulator.CaretColumn == 0) {
                    TerminalDocument doc = GetDocument();
                    int line = doc.CurrentLineNumber - 1;
                    if (line >= 0 && doc.FindLineOrEdge(line).EOLType == EOLType.Continue) {
                        doc.InvalidatedRegion.InvalidateLine(doc.CurrentLineNumber);
                        doc.CurrentLineNumber = line;
                        if (doc.CurrentLine == null)
                            _manipulator.Reset(doc.TerminalWidth);
                        else
                            _manipulator.Load(doc.CurrentLine, doc.CurrentLine.DisplayLength - 1); //NOTE ここはCharLengthだったが同じだと思って改名した
                        doc.InvalidatedRegion.InvalidateLine(doc.CurrentLineNumber);
                    }
                }
                else
                    _manipulator.BackCaret();

                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.HT) {
                _manipulator.CaretColumn = GetNextTabStop(_manipulator.CaretColumn);
                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.SO) {
                return ProcessCharResult.Processed; //以下２つはCharDecoderの中で処理されているはずなので無視
            }
            else if (ch == ControlCode.SI) {
                return ProcessCharResult.Processed;
            }
            else if (ch == ControlCode.NUL) {
                return ProcessCharResult.Processed; //null charは無視 !!CR NULをCR LFとみなす仕様があるが、CR LF CR NULとくることもあって難しい
            }
            else {
                //Debug.WriteLine("Unknown char " + (int)ch);
                //適当なグラフィック表示ほしい
                return ProcessCharResult.Unsupported;
            }
        }
        private void DoLineFeed() {
            _manipulator.EOLType = (_manipulator.EOLType == EOLType.CR || _manipulator.EOLType == EOLType.CRLF) ? EOLType.CRLF : EOLType.LF;
            GLine lineUpdated = GetDocument().UpdateCurrentLine(_manipulator);
            if (lineUpdated != null) {
                this.LogService.TextLogger.WriteLine(lineUpdated);
            }
            GetDocument().LineFeed();

            //カラム保持は必要。サンプル:linuxconf.log
            int col = _manipulator.CaretColumn;
            _manipulator.Load(GetDocument().CurrentLine, col);
        }
        private void DoCarriageReturn() {
            _manipulator.CarriageReturn();
            _manipulator.EOLType = EOLType.CR;  // will be changed to CRLF in DoLineFeed()
        }

        protected ProcessCharResult ProcessEscapeSequence(char code, char[] seq, int offset) {
            string param;
            switch (code) {
                case '[':
                    if (seq.Length - offset - 1 >= 0) {
                        param = new string(seq, offset, seq.Length - offset - 1);
                        return ProcessAfterCSI(param, seq[seq.Length - 1]);
                    }
                    break;
                //throw new UnknownEscapeSequenceException(String.Format("unknown command after CSI {0}", code));
                case ']':
                    if (seq.Length - offset - 1 >= 0) {
                        param = new string(seq, offset, seq.Length - offset - 1);
                        return ProcessAfterOSC(param, seq[seq.Length - 1]);
                    }
                    break;
                case '=':
                    ChangeMode(TerminalMode.Application);
                    return ProcessCharResult.Processed;
                case '>':
                    ChangeMode(TerminalMode.Normal);
                    return ProcessCharResult.Processed;
                case 'E':
                    ProcessNextLine();
                    return ProcessCharResult.Processed;
                case 'M':
                    ReverseIndex();
                    return ProcessCharResult.Processed;
                case 'D':
                    Index();
                    return ProcessCharResult.Processed;
                case '7':
                    _screenBuffer.SaveCursor();
                    return ProcessCharResult.Processed;
                case '8':
                    _screenBuffer.RestoreCursor();
                    return ProcessCharResult.Processed;
                case 'c':
                    FullReset();
                    return ProcessCharResult.Processed;
                case 'F':
                    if (seq.Length == offset) { //パラメータなしの場合
                        ProcessCursorPosition(1, 1);
                        return ProcessCharResult.Processed;
                    }
                    else if (seq.Length > offset && seq[offset] == ' ')
                        return ProcessCharResult.Processed; //7/8ビットコントロールは常に両方をサポート
                    break;
                case 'G':
                    if (seq.Length > offset && seq[offset] == ' ')
                        return ProcessCharResult.Processed; //7/8ビットコントロールは常に両方をサポート
                    break;
                case 'L':
                    if (seq.Length > offset && seq[offset] == ' ')
                        return ProcessCharResult.Processed; //VT100は最初からOK
                    break;
                case 'H':
                    SetTabStop(_manipulator.CaretColumn, true);
                    return ProcessCharResult.Processed;
            }

            return ProcessCharResult.Unsupported;
        }

        protected override void ChangeMode(TerminalMode mode) {
            if (_terminalMode == mode)
                return;

            if (mode == TerminalMode.Normal) {
                GetDocument().ClearScrollingRegion();
                GetConnection().TerminalOutput.Resize(GetDocument().TerminalWidth, GetDocument().TerminalHeight); //たとえばemacs起動中にリサイズし、シェルへ戻るとシェルは新しいサイズを認識していない
                //RMBoxで確認されたことだが、無用に後方にドキュメントを広げてくる奴がいる。カーソルを123回後方へ、など。
                //場当たり的だが、ノーマルモードに戻る際に後ろの空行を削除することで対応する。
                GLine l = GetDocument().LastLine;
                while (l != null && l.DisplayLength == 0 && l.ID > GetDocument().CurrentLineNumber)
                    l = l.PrevLine;

                if (l != null)
                    l = l.NextLine;
                if (l != null)
                    GetDocument().RemoveAfter(l.ID);

                GetDocument().IsApplicationMode = false;
            }
            else {
                GetDocument().ApplicationModeBackColor = ColorSpec.Default;
                GetDocument().SetScrollingRegion(0, GetDocument().TerminalHeight - 1);
                GetDocument().IsApplicationMode = true;
            }

            GetDocument().InvalidateAll();

            _terminalMode = mode;
        }

        protected ProcessCharResult ProcessAfterCSI(string param, char code) {
            switch (code) {
                case 'c':
                    ProcessDeviceAttributes(param);
                    return ProcessCharResult.Processed;
                case 'm': //SGR
                    ProcessSGR(param);
                    return ProcessCharResult.Processed;
                case 'h':
                case 'l':
                    return ProcessDECSETMulti(param, code);
                case 'r':
                    if (param.Length > 0 && param[0] == '?')
                        return ProcessRestoreDECSET(param.Substring(1), code);
                    else {
                        ProcessSetScrollingRegion(param);
                        return ProcessCharResult.Processed;
                    }
                case 's':
                    if (param.Length > 0 && param[0] == '?')
                        return ProcessSaveDECSET(param.Substring(1), code);
                    else
                        return ProcessCharResult.Unsupported;
                case 'n':
                    ProcessDeviceStatusReport(param);
                    return ProcessCharResult.Processed;
                case 'A':
                case 'B':
                case 'C':
                case 'D':
                case 'E':
                case 'F':
                    ProcessCursorMove(param, code);
                    return ProcessCharResult.Processed;
                case 'H':
                case 'f': //fは本当はxterm固有
                    ProcessCursorPosition(param);
                    return ProcessCharResult.Processed;
                case 'J':
                    ProcessEraseInDisplay(param);
                    return ProcessCharResult.Processed;
                case 'K':
                    ProcessEraseInLine(param);
                    return ProcessCharResult.Processed;
                case 'L':
                    ProcessInsertLines(param);
                    return ProcessCharResult.Processed;
                case 'M':
                    ProcessDeleteLines(param);
                    return ProcessCharResult.Processed;
                case 'd':
                    ProcessLinePositionAbsolute(param);
                    return ProcessCharResult.Processed;
                case 'G':
                case '`': //CSI Gは実際に来たことがあるが、これは来たことがない。いいのか？
                    ProcessLineColumnAbsolute(param);
                    return ProcessCharResult.Processed;
                case 'X':
                    ProcessEraseChars(param);
                    return ProcessCharResult.Processed;
                case 'P':
                    _manipulator.DeleteChars(_manipulator.CaretColumn, ParseInt(param, 1), _currentdecoration);
                    return ProcessCharResult.Processed;
                case 'p':
                    return SoftTerminalReset(param);
                case '@':
                    _manipulator.InsertBlanks(_manipulator.CaretColumn, ParseInt(param, 1), _currentdecoration);
                    return ProcessCharResult.Processed;
                case 'I':
                    ProcessForwardTab(param);
                    return ProcessCharResult.Processed;
                case 'Z':
                    ProcessBackwardTab(param);
                    return ProcessCharResult.Processed;
                case 'S':
                    ProcessScrollUp(param);
                    return ProcessCharResult.Processed;
                case 'T':
                    ProcessScrollDown(param);
                    return ProcessCharResult.Processed;
                case 'g':
                    ProcessTabClear(param);
                    return ProcessCharResult.Processed;
                case 't':
                    //!!パラメータによって無視してよい場合と、応答を返すべき場合がある。応答の返し方がよくわからないので保留中
                    return ProcessCharResult.Processed;
                case 'U': //これはSFUでしか確認できてない
                    ProcessCursorPosition(GetDocument().TerminalHeight, 1);
                    return ProcessCharResult.Processed;
                case 'u': //SFUでのみ確認。特にbは続く文字を繰り返すらしいが、意味のある動作になっているところを見ていない
                case 'b':
                    return ProcessCharResult.Processed;
                default:
                    return ProcessCharResult.Unsupported;
            }
        }

        protected void ProcessDeviceAttributes(string param) {
            if (param.StartsWith(">")) {
                byte[] data = Encoding.ASCII.GetBytes(" [>82;1;0c");
                data[0] = 0x1B; //ESC
                TransmitDirect(data);
            }
            else {
                byte[] data = Encoding.ASCII.GetBytes(" [?1;2c"); //なんかよくわからないがMindTerm等をみるとこれでいいらしい
                data[0] = 0x1B; //ESC
                TransmitDirect(data);
            }
        }

        protected void ProcessDeviceStatusReport(string param) {
            string response;
            if (param == "5")
                response = " [0n"; //これでOKの意味らしい
            else if (param == "6")
                response = String.Format(" [{0};{1}R", GetDocument().CurrentLineNumber - GetDocument().TopLineNumber + 1, _manipulator.CaretColumn + 1);
            else
                throw new UnknownEscapeSequenceException("DSR " + param);

            byte[] data = Encoding.ASCII.GetBytes(response);
            data[0] = 0x1B; //ESC
            TransmitDirect(data);
        }

        protected void ProcessCursorMove(string param, char method) {
            int count = ParseInt(param, 1); //パラメータが省略されたときの移動量は１

            int column = _manipulator.CaretColumn;
            switch (method) {
                case 'A':
                    GetDocument().UpdateCurrentLine(_manipulator);
                    GetDocument().CurrentLineNumber = (GetDocument().CurrentLineNumber - count);
                    _manipulator.Load(GetDocument().CurrentLine, column);
                    break;
                case 'B':
                    GetDocument().UpdateCurrentLine(_manipulator);
                    GetDocument().CurrentLineNumber = (GetDocument().CurrentLineNumber + count);
                    _manipulator.Load(GetDocument().CurrentLine, column);
                    break;
                case 'C': {
                        int newvalue = column + count;
                        if (newvalue >= GetDocument().TerminalWidth)
                            newvalue = GetDocument().TerminalWidth - 1;
                        _manipulator.CaretColumn = newvalue;
                    }
                    break;
                case 'D': {
                        int newvalue = column - count;
                        if (newvalue < 0)
                            newvalue = 0;
                        _manipulator.CaretColumn = newvalue;
                    }
                    break;
            }
        }

        //CSI H
        protected void ProcessCursorPosition(string param) {
            IntPair t = ParseIntPair(param, 1, 1);
            int row = t.first, col = t.second;
            if (_scrollRegionRelative && GetDocument().ScrollingTop != -1) {
                row += GetDocument().ScrollingTop;
            }

            if (row < 1)
                row = 1;
            else if (row > GetDocument().TerminalHeight)
                row = GetDocument().TerminalHeight;
            if (col < 1)
                col = 1;
            else if (col > GetDocument().TerminalWidth)
                col = GetDocument().TerminalWidth;
            ProcessCursorPosition(row, col);
        }
        protected void ProcessCursorPosition(int row, int col) {
            GetDocument().UpdateCurrentLine(_manipulator);
            GetDocument().CurrentLineNumber = (GetDocument().TopLineNumber + row - 1);
            //int cc = GetDocument().CurrentLine.DisplayPosToCharPos(col-1);
            //Debug.Assert(cc>=0);
            _manipulator.Load(GetDocument().CurrentLine, col - 1);
        }

        //CSI J
        protected void ProcessEraseInDisplay(string param) {
            int d = ParseInt(param, 0);

            TerminalDocument doc = GetDocument();
            int cur = doc.CurrentLineNumber;
            int top = doc.TopLineNumber;
            int bottom = top + doc.TerminalHeight;
            int col = _manipulator.CaretColumn;
            switch (d) {
                case 0: //erase below
                    {
                        if (col == 0 && cur == top)
                            goto ERASE_ALL;

                        EraseRight();
                        doc.UpdateCurrentLine(_manipulator);
                        doc.EnsureLine(bottom - 1);
                        doc.RemoveAfter(bottom);
                        doc.ClearRange(cur + 1, bottom, _currentdecoration);
                        _manipulator.Load(doc.CurrentLine, col);
                    }
                    break;
                case 1: //erase above
                    {
                        if (col == doc.TerminalWidth - 1 && cur == bottom - 1)
                            goto ERASE_ALL;

                        EraseLeft();
                        doc.UpdateCurrentLine(_manipulator);
                        doc.ClearRange(top, cur, _currentdecoration);
                        _manipulator.Load(doc.CurrentLine, col);
                    }
                    break;
                case 2: //erase all
                ERASE_ALL: {
                        GetDocument().ApplicationModeBackColor =
                            (_currentdecoration != null) ? _currentdecoration.GetBackColorSpec() : ColorSpec.Default;

                        doc.UpdateCurrentLine(_manipulator);
                        //if(_homePositionOnCSIJ2) { //SFUではこうなる
                        //	ProcessCursorPosition(1,1); 
                        //	col = 0;
                        //}
                        doc.EnsureLine(bottom - 1);
                        doc.RemoveAfter(bottom);
                        doc.ClearRange(top, bottom, _currentdecoration);
                        _manipulator.Load(doc.CurrentLine, col);
                    }
                    break;
                default:
                    throw new UnknownEscapeSequenceException(String.Format("unknown ED option {0}", param));
            }

        }

        //CSI K
        private void ProcessEraseInLine(string param) {
            int d = ParseInt(param, 0);

            switch (d) {
                case 0: //erase right
                    EraseRight();
                    break;
                case 1: //erase left
                    EraseLeft();
                    break;
                case 2: //erase all
                    EraseLine();
                    break;
                default:
                    throw new UnknownEscapeSequenceException(String.Format("unknown EL option {0}", param));
            }
        }

        private void EraseRight() {
            _manipulator.FillSpace(_manipulator.CaretColumn, _manipulator.BufferSize, _currentdecoration);
        }

        private void EraseLeft() {
            _manipulator.FillSpace(0, _manipulator.CaretColumn + 1, _currentdecoration);
        }

        private void EraseLine() {
            _manipulator.FillSpace(0, _manipulator.BufferSize, _currentdecoration);
        }

        protected void Index() {
            GetDocument().UpdateCurrentLine(_manipulator);
            int current = GetDocument().CurrentLineNumber;
            if (current == GetDocument().TopLineNumber + GetDocument().TerminalHeight - 1 || current == GetDocument().ScrollingBottom)
                GetDocument().ScrollDown();
            else
                GetDocument().CurrentLineNumber = current + 1;
            _manipulator.Load(GetDocument().CurrentLine, _manipulator.CaretColumn);
        }
        protected void ReverseIndex() {
            GetDocument().UpdateCurrentLine(_manipulator);
            int current = GetDocument().CurrentLineNumber;
            if (current == GetDocument().TopLineNumber || current == GetDocument().ScrollingTop)
                GetDocument().ScrollUp();
            else
                GetDocument().CurrentLineNumber = current - 1;
            _manipulator.Load(GetDocument().CurrentLine, _manipulator.CaretColumn);
        }

        protected void ProcessSetScrollingRegion(string param) {
            int height = GetDocument().TerminalHeight;
            IntPair v = ParseIntPair(param, 1, height);

            if (v.first < 1)
                v.first = 1;
            else if (v.first > height)
                v.first = height;
            if (v.second < 1)
                v.second = 1;
            else if (v.second > height)
                v.second = height;
            if (v.first > v.second) { //問答無用でエラーが良いようにも思うが
                int t = v.first;
                v.first = v.second;
                v.second = t;
            }

            //指定は1-originだが処理は0-origin
            GetDocument().SetScrollingRegion(v.first - 1, v.second - 1);
        }

        protected void ProcessNextLine() {
            GetDocument().UpdateCurrentLine(_manipulator);
            GetDocument().CurrentLineNumber = (GetDocument().CurrentLineNumber + 1);
            _manipulator.Load(GetDocument().CurrentLine, 0);
        }


        protected ProcessCharResult ProcessAfterOSC(string param, char code) {
            int semicolon = param.IndexOf(';');
            if (semicolon == -1)
                return ProcessCharResult.Unsupported;

            string ps = param.Substring(0, semicolon);
            string pt = param.Substring(semicolon + 1);
            if (ps == "0" || ps == "2") {
                IDynamicCaptionFormatter[] fmts = TerminalEmulatorPlugin.Instance.DynamicCaptionFormatter;
                TerminalDocument doc = GetDocument();

                if (fmts.Length > 0) {
                    ITerminalSettings settings = GetTerminalSettings();
                    string title = fmts[0].FormatCaptionUsingWindowTitle(GetConnection().Destination, settings, pt);
                    _afterExitLockActions.Add(new CaptionChanger(GetTerminalSettings(), title).Do);
                }
                //Quick Test
                //_afterExitLockActions.Add(new AfterExitLockDelegate(new CaptionChanger(GetTerminalSettings(), pt).Do));

                return ProcessCharResult.Processed;
            }
            else if (ps == "1")
                return ProcessCharResult.Processed; //Set Icon Nameというやつだが無視でよさそう
            else if (ps == "4") {
                // パレット変更
                //   形式: OSC 4 ; 色番号 ; 色指定 ST
                //     色番号: 0～255
                //     色指定: 以下の形式のどれか
                //       #rgb
                //       #rrggbb
                //       #rrrgggbbb
                //       #rrrrggggbbbb
                //       rgb:r/g/b
                //       rgb:rr/gg/bb
                //       rgb:rrr/ggg/bbb
                //       rgb:rrrr/gggg/bbbb
                //       他にも幾つか形式があるけれど、通常はこれで十分と思われる。
                //       他の形式は XParseColor(1) を参照
                //
                // 参考: http://ttssh2.sourceforge.jp/manual/ja/about/ctrlseq.html#OSC
                //
                while ((semicolon = pt.IndexOf(';')) != -1) {
                    string pv = pt.Substring(semicolon + 1);
                    int pn;
                    if (Int32.TryParse(pt.Substring(0, semicolon), out pn) && pn >= 0 && pn <= 255) {
                        if ((semicolon = pv.IndexOf(';')) != -1) {
                            pt = pv.Substring(semicolon + 1);
                            pv = pv.Substring(0, semicolon);
                        }
                        else {
                            pt = pv;
                        }
                        int r, g, b;
                        if (pv.StartsWith("#")) {
                            switch (pv.Length) {
                                case 4: // #rgb
                                    if (Int32.TryParse(pv.Substring(1, 1), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out r) &&
                                        Int32.TryParse(pv.Substring(2, 1), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out g) &&
                                        Int32.TryParse(pv.Substring(3, 1), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out b)) {
                                        r <<= 4;
                                        g <<= 4;
                                        b <<= 4;
                                    }
                                    else {
                                        return ProcessCharResult.Unsupported;
                                    }
                                    break;
                                case 7: // #rrggbb
                                    if (Int32.TryParse(pv.Substring(1, 2), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out r) &&
                                        Int32.TryParse(pv.Substring(3, 2), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out g) &&
                                        Int32.TryParse(pv.Substring(5, 2), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out b)) {
                                    }
                                    else {
                                        return ProcessCharResult.Unsupported;
                                    }
                                    break;
                                case 10: // #rrrgggbbb
                                    if (Int32.TryParse(pv.Substring(1, 3), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out r) &&
                                        Int32.TryParse(pv.Substring(4, 3), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out g) &&
                                        Int32.TryParse(pv.Substring(7, 3), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out b)) {
                                        r >>= 4;
                                        g >>= 4;
                                        b >>= 4;
                                    }
                                    else {
                                        return ProcessCharResult.Unsupported;
                                    }
                                    break;
                                case 13: // #rrrrggggbbbb
                                    if (Int32.TryParse(pv.Substring(1, 4), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out r) &&
                                        Int32.TryParse(pv.Substring(5, 4), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out g) &&
                                        Int32.TryParse(pv.Substring(9, 4), NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out b)) {
                                        r >>= 8;
                                        g >>= 8;
                                        b >>= 8;
                                    }
                                    else {
                                        return ProcessCharResult.Unsupported;
                                    }
                                    break;
                                default:
                                    return ProcessCharResult.Unsupported;
                            }
                        }
                        else if (pv.StartsWith("rgb:")) { // rgb:rr/gg/bb
                            string[] vals = pv.Substring(4).Split(new Char[] { '/' });
                            if (vals.Length == 3
                                && vals[0].Length == vals[1].Length
                                && vals[0].Length == vals[2].Length
                                && vals[0].Length > 0
                                && vals[0].Length <= 4
                                && Int32.TryParse(vals[0], NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out r)
                                && Int32.TryParse(vals[1], NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out g)
                                && Int32.TryParse(vals[2], NumberStyles.AllowHexSpecifier, NumberFormatInfo.InvariantInfo, out b)) {
                                switch (vals[0].Length) {
                                    case 1:
                                        r <<= 4;
                                        g <<= 4;
                                        b <<= 4;
                                        break;
                                    case 3:
                                        r >>= 4;
                                        g >>= 4;
                                        b >>= 4;
                                        break;
                                    case 4:
                                        r >>= 8;
                                        g >>= 8;
                                        b >>= 8;
                                        break;
                                }
                            }
                            else {
                                return ProcessCharResult.Unsupported;
                            }
                        }
                        else {
                            return ProcessCharResult.Unsupported;
                        }
                        GetRenderProfile().ESColorSet[pn] = new ESColor(Color.FromArgb(r, g, b), true);
                    }
                    else {
                        return ProcessCharResult.Unsupported;
                    }
                }
                return ProcessCharResult.Processed;
            }
            else
                return ProcessCharResult.Unsupported;
        }

        protected void ProcessSGR(string param) {
            int state = 0, target = 0, r = 0, g = 0, b = 0;
            string[] ps = param.Split(';');
            TextDecoration dec = _currentdecoration;
            foreach (string cmd in ps) {
                int code = ParseSGRCode(cmd);
                if (state != 0) {
                    switch (state) {
                        case 1:
                            if (code == 5) { // select indexed color
                                state = 2;
                            }
                            else if (code == 2) { // select RGB color
                                state = 3;  // read R value
                            }
                            else {
                                Debug.WriteLine("Invalid SGR code : {0}", code);
                                goto Apply;
                            }
                            break;
                        case 2:
                            if (code < 256) {
                                if (target == 3) {
                                    dec = SelectForeColor(dec, code);
                                }
                                else if (target == 4) {
                                    dec = SelectBackgroundColor(dec, code);
                                }
                            }
                            state = 0;
                            target = 0;
                            break;
                        case 3:
                            if (code < 256) {
                                r = code;
                                state = 4;  // read G value
                            }
                            else {
                                Debug.WriteLine("Invalid SGR R value : {0}", code);
                                goto Apply;
                            }
                            break;
                        case 4:
                            if (code < 256) {
                                g = code;
                                state = 5;  // read B value
                            }
                            else {
                                Debug.WriteLine("Invalid SGR G value : {0}", code);
                                goto Apply;
                            }
                            break;
                        case 5:
                            if (code < 256) {
                                b = code;
                                if (target == 3) {
                                    dec = SetForeColorByRGB(dec, r, g, b);
                                }
                                else if (target == 4) {
                                    dec = SetBackColorByRGB(dec, r, g, b);
                                }
                                state = 0;
                                target = 0;
                            }
                            else {
                                Debug.WriteLine("Invalid SGR B value : {0}", code);
                                goto Apply;
                            }
                            break;
                    }
                }
                else {
                    switch (code) {
                        case 8: // concealed characters (ECMA-48,VT300)
                            dec = dec.GetCopyWithHidden(true);
                            break;
                        case 28: // revealed characters (ECMA-48)
                            dec = dec.GetCopyWithHidden(false);
                            break;
                        case 38: // Set foreground color (XTERM,ISO-8613-3)
                            state = 1;  // start reading subsequent values
                            target = 3; // set foreground color
                            break;
                        case 48: // Set background color (XTERM,ISO-8613-3)
                            state = 1;  // start reading subsequent values
                            target = 4; // set background color
                            break;
                        case 90: // Set foreground color to Black (XTERM)
                        case 91: // Set foreground color to Red (XTERM)
                        case 92: // Set foreground color to Green (XTERM)
                        case 93: // Set foreground color to Yellow (XTERM)
                        case 94: // Set foreground color to Blue (XTERM)
                        case 95: // Set foreground color to Magenta (XTERM)
                        case 96: // Set foreground color to Cyan (XTERM)
                        case 97: // Set foreground color to White (XTERM)
                            dec = SelectForeColor(dec, code - 90 + 8);
                            break;
                        case 100: // Set background color to Black (XTERM)
                        case 101: // Set background color to Red (XTERM)
                        case 102: // Set background color to Green (XTERM)
                        case 103: // Set background color to Yellow (XTERM)
                        case 104: // Set background color to Blue (XTERM)
                        case 105: // Set background color to Magenta (XTERM)
                        case 106: // Set background color to Cyan (XTERM)
                        case 107: // Set background color to White (XTERM)
                            dec = SelectBackgroundColor(dec, code - 100 + 8);
                            break;
                        default:
                            ProcessSGRParameterANSI(code, ref dec);
                            break;
                    }
                }
            }
        Apply:
            _currentdecoration = dec;
        }

        protected int ParseSGRCode(string param) {
            if (param.Length == 0)
                return 0;
            else if (param.Length == 1)
                return param[0] - '0';
            else if (param.Length == 2)
                return (param[0] - '0') * 10 + (param[1] - '0');
            else if (param.Length == 3)
                return (param[0] - '0') * 100 + (param[1] - '0') * 10 + (param[2] - '0');
            else
                throw new UnknownEscapeSequenceException(String.Format("unknown SGR parameter {0}", param));
        }

        protected void ProcessSGRParameterANSI(int code, ref TextDecoration dec) {
            switch (code) {
                case 0: // default rendition (implementation-defined) (ECMA-48,VT100,VT220)
                    dec = TextDecoration.Default;
                    break;
                case 1: // bold or increased intensity (ECMA-48,VT100,VT220)
                    dec = dec.GetCopyWithBold(true);
                    break;
                case 2: // faint, decreased intensity or second colour (ECMA-48)
                    break;
                case 3: // italicized (ECMA-48)
                    break;
                case 4: // singly underlined (ECMA-48,VT100,VT220)
                    dec = dec.GetCopyWithUnderline(true);
                    break;
                case 5: // slowly blinking (ECMA-48,VT100,VT220)
                case 6: // rapidly blinking (ECMA-48)
                    dec = dec.GetCopyWithBlink(true);
                    break;
                case 7: // negative image (ECMA-48,VT100,VT220)
                    dec = dec.GetCopyWithInverted(true);
                    break;
                case 8: // concealed characters (ECMA-48,VT300)
                case 9: // crossed-out (ECMA-48)
                case 10: // primary (default) font (ECMA-48)
                case 11: // first alternative font (ECMA-48)
                case 12: // second alternative font (ECMA-48)
                case 13: // third alternative font (ECMA-48)
                case 14: // fourth alternative font (ECMA-48)
                case 15: // fifth alternative font (ECMA-48)
                case 16: // sixth alternative font (ECMA-48)
                case 17: // seventh alternative font (ECMA-48)
                case 18: // eighth alternative font (ECMA-48)
                case 19: // ninth alternative font (ECMA-48)
                case 20: // Fraktur (Gothic) (ECMA-48)
                case 21: // doubly underlined (ECMA-48)
                    break;
                case 22: // normal colour or normal intensity (neither bold nor faint) (ECMA-48,VT220,VT300)
                    dec = TextDecoration.Default;
                    break;
                case 23: // not italicized, not fraktur (ECMA-48)
                    break;
                case 24: // not underlined (neither singly nor doubly) (ECMA-48,VT220,VT300)
                    dec = dec.GetCopyWithUnderline(false);
                    break;
                case 25: // steady (not blinking) (ECMA-48,VT220,VT300)
                    dec = dec.GetCopyWithBlink(false);
                    break;
                case 26: // reserved (ECMA-48)
                    break;
                case 27: // positive image (ECMA-48,VT220,VT300)
                    dec = dec.GetCopyWithInverted(false);
                    break;
                case 28: // revealed characters (ECMA-48)
                case 29: // not crossed out (ECMA-48)
                    break;
                case 30: // black display (ECMA-48)
                case 31: // red display (ECMA-48)
                case 32: // green display (ECMA-48)
                case 33: // yellow display (ECMA-48)
                case 34: // blue display (ECMA-48)
                case 35: // magenta display (ECMA-48)
                case 36: // cyan display (ECMA-48)
                case 37: // white display (ECMA-48)
                    dec = SelectForeColor(dec, code - 30);
                    break;
                case 38: // reserved (ECMA-48)
                    break;
                case 39: // default display colour (implementation-defined) (ECMA-48)
                    dec = dec.GetCopyWithForeColor(ColorSpec.Default);
                    break;
                case 40: // black background (ECMA-48)
                case 41: // red background (ECMA-48)
                case 42: // green background (ECMA-48)
                case 43: // yellow background (ECMA-48)
                case 44: // blue background (ECMA-48)
                case 45: // magenta background (ECMA-48)
                case 46: // cyan background (ECMA-48)
                case 47: // white background (ECMA-48)
                    dec = SelectBackgroundColor(dec, code - 40);
                    break;
                case 48: // reserved (ECMA-48)
                    break;
                case 49: // default background colour (implementation-defined) (ECMA-48)
                    dec = dec.GetCopyWithBackColor(ColorSpec.Default);
                    break;
                case 50: // reserved (ECMA-48)
                case 51: // framed (ECMA-48)
                case 52: // encircled (ECMA-48)
                case 53: // overlined (ECMA-48)
                case 54: // not framed, not encircled (ECMA-48)
                case 55: // not overlined (ECMA-48)
                case 56: // reserved (ECMA-48)
                case 57: // reserved (ECMA-48)
                case 58: // reserved (ECMA-48)
                case 59: // reserved (ECMA-48)
                case 60: // ideogram underline or right side line (ECMA-48)
                case 61: // ideogram double underline or double line on the right side (ECMA-48)
                case 62: // ideogram overline or left side line (ECMA-48)
                case 63: // ideogram double overline or double line on the left side (ECMA-48)
                case 64: // ideogram stress marking (ECMA-48)
                case 65: // cancels the effect of the rendition aspects established by parameter values 60 to 64 (ECMA-48)
                    break;
                default:
                    // other values are ignored without notification to the user
                    Debug.WriteLine("unknown SGR code (ANSI) : {0}", code);
                    break;
            }
        }

        protected TextDecoration SelectForeColor(TextDecoration dec, int index) {
            return dec.GetCopyWithForeColor(new ColorSpec(index));
        }

        protected TextDecoration SelectBackgroundColor(TextDecoration dec, int index) {
            return dec.GetCopyWithBackColor(new ColorSpec(index));
        }

        private TextDecoration SetForeColorByRGB(TextDecoration dec, int r, int g, int b) {
            return dec.GetCopyWithForeColor(new ColorSpec(Color.FromArgb(r, g, b)));
        }

        private TextDecoration SetBackColorByRGB(TextDecoration dec, int r, int g, int b) {
            return dec.GetCopyWithBackColor(new ColorSpec(Color.FromArgb(r, g, b)));
        }

        protected ProcessCharResult ProcessDECSET(string param, char code) {
            switch (param) {
                case "25":
                    return ProcessCharResult.Processed; //!!Show/Hide Cursorだがとりあえず無視
                case "1":
                    ChangeCursorKeyMode(code == 'h' ? TerminalMode.Application : TerminalMode.Normal);
                    return ProcessCharResult.Processed;
            }

            bool set = code == 'h';

            switch (param) {
                case "1047":	//Alternate Buffer
                    if (set) {
                        _screenBuffer.SwitchBuffer(true);
                        // XTerm doesn't clear screen.
                    }
                    else {
                        ClearScreen();
                        _screenBuffer.SwitchBuffer(false);
                    }
                    return ProcessCharResult.Processed;
                case "1048":	//Save/Restore Cursor
                    if (set)
                        _screenBuffer.SaveCursor();
                    else
                        _screenBuffer.RestoreCursor();
                    return ProcessCharResult.Processed;
                case "1049":	//Save/Restore Cursor and Alternate Buffer
                    if (set) {
                        _screenBuffer.SaveCursor();
                        _screenBuffer.SwitchBuffer(true);
                        ClearScreen();
                    }
                    else {
                        // XTerm doesn't clear screen for enabling copy/paste from the alternate buffer.
                        // But we need ClearScreen for emulating the buffer-switch.
                        ClearScreen();
                        _screenBuffer.SwitchBuffer(false);
                        _screenBuffer.RestoreCursor();
                    }
                    return ProcessCharResult.Processed;
                case "1000": // DEC VT200 compatible: Send button press and release event with mouse position.
                    ResetMouseTracking(set ?
                        MouseTrackingManager.MouseTrackingState.Normal :
                        MouseTrackingManager.MouseTrackingState.Off);
                    return ProcessCharResult.Processed;
                case "1001": // DEC VT200 highlight tracking
                    // Not supported
                    ResetMouseTracking(MouseTrackingManager.MouseTrackingState.Off);
                    return ProcessCharResult.Processed;
                case "1002": // Button-event tracking: Send button press, release, and drag event.
                    ResetMouseTracking(set ?
                        MouseTrackingManager.MouseTrackingState.Drag :
                        MouseTrackingManager.MouseTrackingState.Off);
                    return ProcessCharResult.Processed;
                case "1003": // Any-event tracking: Send button press, release, and motion.
                    ResetMouseTracking(set ?
                        MouseTrackingManager.MouseTrackingState.Any :
                        MouseTrackingManager.MouseTrackingState.Off);
                    return ProcessCharResult.Processed;
                case "1004": // Send FocusIn/FocusOut events
                    _focusReporting.SetFocusReportingMode(set);
                    return ProcessCharResult.Processed;
                case "1005": // Enable UTF8 Mouse Mode
                    SetMouseTrackingProtocol(set ?
                        MouseTrackingManager.MouseTrackingProtocol.Utf8 :
                        MouseTrackingManager.MouseTrackingProtocol.Normal);
                    return ProcessCharResult.Processed;
                case "1006": // Enable SGR Extended Mouse Mode
                    SetMouseTrackingProtocol(set ?
                        MouseTrackingManager.MouseTrackingProtocol.Sgr :
                        MouseTrackingManager.MouseTrackingProtocol.Normal);
                    return ProcessCharResult.Processed;
                case "1015": // Enable UTF8 Extended Mouse Mode
                    SetMouseTrackingProtocol(set ?
                        MouseTrackingManager.MouseTrackingProtocol.Urxvt :
                        MouseTrackingManager.MouseTrackingProtocol.Normal);
                    return ProcessCharResult.Processed;
                case "1034":	// Input 8 bits
                    return ProcessCharResult.Processed;
                case "2004":    // Set/Reset bracketed paste mode
                    _bracketedPasteMode.SetBracketedPasteMode(set);
                    return ProcessCharResult.Processed;
                case "3":	//132 Column Mode
                    return ProcessCharResult.Processed;
                case "4":	//Smooth Scroll なんのことやら
                    return ProcessCharResult.Processed;
                case "5":
                    SetReverseVideo(set);
                    return ProcessCharResult.Processed;
                case "6":	//Origin Mode
                    _scrollRegionRelative = set;
                    return ProcessCharResult.Processed;
                case "7":
                    _wrapAroundMode = set;
                    return ProcessCharResult.Processed;
                case "12":
                    //一応報告あったので。SETMODEの12ならローカルエコーなんだがな
                    return ProcessCharResult.Processed;
                case "47":
                    _screenBuffer.SwitchBuffer(set);
                    return ProcessCharResult.Processed;
                default:
                    return ProcessCharResult.Unsupported;
            }
        }

        protected ProcessCharResult ProcessSaveDECSET(string param, char code) {
            switch (param) {
                case "1047":
                case "47":
                    _screenBuffer.SaveBufferMode();
                    break;
            }
            return ProcessCharResult.Processed;
        }

        protected ProcessCharResult ProcessRestoreDECSET(string param, char code) {
            switch (param) {
                case "1047":
                case "47":
                    _screenBuffer.RestoreBufferMode();
                    break;
            }
            return ProcessCharResult.Processed;
        }

        private void ResetMouseTracking(MouseTrackingManager.MouseTrackingState newState) {
            _mouseTracking.SetMouseTrackingState(newState);
        }

        private void SetMouseTrackingProtocol(MouseTrackingManager.MouseTrackingProtocol newProtocol) {
            _mouseTracking.SetMouseTrackingProtocol(newProtocol);
        }

        private void ProcessLinePositionAbsolute(string param) {
            foreach (string p in param.Split(';')) {
                int row = ParseInt(p, 1);
                if (row < 1)
                    row = 1;
                if (row > GetDocument().TerminalHeight)
                    row = GetDocument().TerminalHeight;

                int col = _manipulator.CaretColumn;

                //以下はCSI Hとほぼ同じ
                GetDocument().UpdateCurrentLine(_manipulator);
                GetDocument().CurrentLineNumber = (GetDocument().TopLineNumber + row - 1);
                _manipulator.Load(GetDocument().CurrentLine, col);
            }
        }
        private void ProcessLineColumnAbsolute(string param) {
            foreach (string p in param.Split(';')) {
                int n = ParseInt(p, 1);
                if (n < 1)
                    n = 1;
                if (n > GetDocument().TerminalWidth)
                    n = GetDocument().TerminalWidth;
                _manipulator.CaretColumn = n - 1;
            }
        }
        private void ProcessEraseChars(string param) {
            int n = ParseInt(param, 1);
            int s = _manipulator.CaretColumn;
            for (int i = 0; i < n; i++) {
                _manipulator.PutChar(UnicodeChar.ASCII_SPACE, _currentdecoration);
                if (_manipulator.CaretColumn >= _manipulator.BufferSize)
                    break;
            }
            _manipulator.CaretColumn = s;
        }
        private void ProcessScrollUp(string param) {
            int d = ParseInt(param, 1);

            TerminalDocument doc = GetDocument();
            int caret_col = _manipulator.CaretColumn;
            int offset = doc.CurrentLineNumber - doc.TopLineNumber;
            doc.UpdateCurrentLine(_manipulator);
            if (doc.ScrollingBottom == -1)
                doc.SetScrollingRegion(0, GetDocument().TerminalHeight - 1);
            for (int i = 0; i < d; i++) {
                doc.ScrollDown(doc.ScrollingTop, doc.ScrollingBottom); // TerminalDocument's "Scroll-Down" means XTerm's "Scroll-Up"
                doc.CurrentLineNumber = doc.TopLineNumber + offset; // find correct GLine
            }
            _manipulator.Load(doc.CurrentLine, caret_col);
        }
        private void ProcessScrollDown(string param) {
            int d = ParseInt(param, 1);

            TerminalDocument doc = GetDocument();
            int caret_col = _manipulator.CaretColumn;
            int offset = doc.CurrentLineNumber - doc.TopLineNumber;
            doc.UpdateCurrentLine(_manipulator);
            if (doc.ScrollingBottom == -1)
                doc.SetScrollingRegion(0, GetDocument().TerminalHeight - 1);
            for (int i = 0; i < d; i++) {
                doc.ScrollUp(doc.ScrollingTop, doc.ScrollingBottom); // TerminalDocument's "Scroll-Up" means XTerm's "Scroll-Down"
                doc.CurrentLineNumber = doc.TopLineNumber + offset; // find correct GLine
            }
            _manipulator.Load(doc.CurrentLine, caret_col);
        }
        private void ProcessForwardTab(string param) {
            int n = ParseInt(param, 1);

            int t = _manipulator.CaretColumn;
            for (int i = 0; i < n; i++)
                t = GetNextTabStop(t);
            if (t >= GetDocument().TerminalWidth)
                t = GetDocument().TerminalWidth - 1;
            _manipulator.CaretColumn = t;
        }
        private void ProcessBackwardTab(string param) {
            int n = ParseInt(param, 1);

            int t = _manipulator.CaretColumn;
            for (int i = 0; i < n; i++)
                t = GetPrevTabStop(t);
            if (t < 0)
                t = 0;
            _manipulator.CaretColumn = t;
        }
        private void ProcessTabClear(string param) {
            if (param == "0")
                SetTabStop(_manipulator.CaretColumn, false);
            else if (param == "3")
                ClearAllTabStop();
        }

        private void InitTabStops() {
            for (int i = 0; i < _tabStops.Length; i++) {
                _tabStops[i] = (i % 8) == 0;
            }
        }
        private void EnsureTabStops(int length) {
            if (length >= _tabStops.Length) {
                bool[] newarray = new bool[Math.Max(length, _tabStops.Length * 2)];
                Array.Copy(_tabStops, 0, newarray, 0, _tabStops.Length);
                for (int i = _tabStops.Length; i < newarray.Length; i++) {
                    newarray[i] = (i % 8) == 0;
                }
                _tabStops = newarray;
            }
        }
        private void SetTabStop(int index, bool value) {
            EnsureTabStops(index + 1);
            _tabStops[index] = value;
        }
        private void ClearAllTabStop() {
            for (int i = 0; i < _tabStops.Length; i++) {
                _tabStops[i] = false;
            }
        }
        protected int GetNextTabStop(int start) {
            EnsureTabStops(Math.Max(start + 1, GetDocument().TerminalWidth));

            int index = start + 1;
            while (index < GetDocument().TerminalWidth) {
                if (_tabStops[index])
                    return index;
                index++;
            }
            return GetDocument().TerminalWidth - 1;
        }
        //これはvt100にはないのでoverrideしない
        protected int GetPrevTabStop(int start) {
            EnsureTabStops(start + 1);

            int index = start - 1;
            while (index > 0) {
                if (_tabStops[index])
                    return index;
                index--;
            }
            return 0;
        }

        protected void ClearScreen() {
            ProcessEraseInDisplay("2");
        }

        //画面の反転
        private void SetReverseVideo(bool reverse) {
            if (reverse == _reverseVideo)
                return;

            _reverseVideo = reverse;
            GetDocument().InvalidatedRegion.InvalidatedAll = true; //全体再描画を促す
        }

        private ProcessCharResult SoftTerminalReset(string param) {
            if (param == "!") {
                FullReset();
                return ProcessCharResult.Processed;
            }
            else
                return ProcessCharResult.Unsupported;
        }

        internal override byte[] SequenceKeyData(Keys modifier, Keys key) {
            if ((int)Keys.F1 <= (int)key && (int)key <= (int)Keys.F12)
                return XtermFunctionKey(modifier, key);
            else if (GUtil.IsCursorKey(key)) {
                byte[] data = ModifyCursorKey(modifier, key);
                if (data != null)
                    return data;
                return SequenceKeyData2(modifier, key);
            }
            else {
                byte[] r = new byte[4];
                r[0] = 0x1B;
                r[1] = (byte)'[';
                r[3] = (byte)'~';
                //このあたりはxtermでは割と違うようだ
                if (key == Keys.Insert)
                    r[2] = (byte)'2';
                else if (key == Keys.Home)
                    r[2] = (byte)'7';
                else if (key == Keys.PageUp)
                    r[2] = (byte)'5';
                else if (key == Keys.Delete)
                    r[2] = (byte)'3';
                else if (key == Keys.End)
                    r[2] = (byte)'8';
                else if (key == Keys.PageDown)
                    r[2] = (byte)'6';
                else
                    throw new ArgumentException("unknown key " + key.ToString());
                return r;
            }
        }


        private static string[] FUNCTIONKEY_MAP = { 
        //     F1    F2    F3    F4    F5    F6    F7    F8    F9    F10   F11  F12
              "11", "12", "13", "14", "15", "17", "18", "19", "20", "21", "23", "24",
        //     F13   F14   F15   F16   F17  F18   F19   F20   F21   F22
              "25", "26", "28", "29", "31", "32", "33", "34", "23", "24" };
        //特定のデータを流すタイプ。現在、カーソルキーとファンクションキーが該当する         
        internal byte[] SequenceKeyData2(Keys modifier, Keys body) {
            if ((int)Keys.F1 <= (int)body && (int)body <= (int)Keys.F12) {
                byte[] r = new byte[5];
                r[0] = 0x1B;
                r[1] = (byte)'[';
                int n = (int)body - (int)Keys.F1;
                if ((modifier & Keys.Shift) != Keys.None)
                    n += 10; //shiftは値を10ずらす
                char tail;
                if (n >= 20)
                    tail = (modifier & Keys.Control) != Keys.None ? '@' : '$';
                else
                    tail = (modifier & Keys.Control) != Keys.None ? '^' : '~';
                string f = FUNCTIONKEY_MAP[n];
                r[2] = (byte)f[0];
                r[3] = (byte)f[1];
                r[4] = (byte)tail;
                return r;
            }
            else if (GUtil.IsCursorKey(body)) {
                byte[] r = new byte[3];
                r[0] = 0x1B;
                if (_cursorKeyMode == TerminalMode.Normal)
                    r[1] = (byte)'[';
                else
                    r[1] = (byte)'O';

                switch (body) {
                    case Keys.Up:
                        r[2] = (byte)'A';
                        break;
                    case Keys.Down:
                        r[2] = (byte)'B';
                        break;
                    case Keys.Right:
                        r[2] = (byte)'C';
                        break;
                    case Keys.Left:
                        r[2] = (byte)'D';
                        break;
                    default:
                        throw new ArgumentException("unknown cursor key code", "key");
                }
                return r;
            }
            else {
                byte[] r = new byte[4];
                r[0] = 0x1B;
                r[1] = (byte)'[';
                r[3] = (byte)'~';
                if (body == Keys.Insert)
                    r[2] = (byte)'1';
                else if (body == Keys.Home)
                    r[2] = (byte)'2';
                else if (body == Keys.PageUp)
                    r[2] = (byte)'3';
                else if (body == Keys.Delete)
                    r[2] = (byte)'4';
                else if (body == Keys.End)
                    r[2] = (byte)'5';
                else if (body == Keys.PageDown)
                    r[2] = (byte)'6';
                else
                    throw new ArgumentException("unknown key " + body.ToString());
                return r;
            }
        }

        private byte[] XtermFunctionKey(Keys modifier, Keys key) {
            int m = 1;
            if ((modifier & Keys.Shift) != Keys.None) {
                m += 1;
            }
            if ((modifier & Keys.Alt) != Keys.None) {
                m += 2;
            }
            if ((modifier & Keys.Control) != Keys.None) {
                m += 4;
            }
            switch (key) {
                case Keys.F1:
                    return XtermFunctionKeyF1ToF4(m, (byte)'P');
                case Keys.F2:
                    return XtermFunctionKeyF1ToF4(m, (byte)'Q');
                case Keys.F3:
                    return XtermFunctionKeyF1ToF4(m, (byte)'R');
                case Keys.F4:
                    return XtermFunctionKeyF1ToF4(m, (byte)'S');
                case Keys.F5:
                    return XtermFunctionKeyF5ToF12(m, (byte)'1', (byte)'5');
                case Keys.F6:
                    return XtermFunctionKeyF5ToF12(m, (byte)'1', (byte)'7');
                case Keys.F7:
                    return XtermFunctionKeyF5ToF12(m, (byte)'1', (byte)'8');
                case Keys.F8:
                    return XtermFunctionKeyF5ToF12(m, (byte)'1', (byte)'9');
                case Keys.F9:
                    return XtermFunctionKeyF5ToF12(m, (byte)'2', (byte)'0');
                case Keys.F10:
                    return XtermFunctionKeyF5ToF12(m, (byte)'2', (byte)'1');
                case Keys.F11:
                    return XtermFunctionKeyF5ToF12(m, (byte)'2', (byte)'3');
                case Keys.F12:
                    return XtermFunctionKeyF5ToF12(m, (byte)'2', (byte)'4');
                default:
                    throw new ArgumentException("unexpected key value : " + key.ToString(), "key");
            }
        }

        private byte[] XtermFunctionKeyF1ToF4(int m, byte c) {
            if (m > 1) {
                return new byte[] { 0x1b, (byte)'[', (byte)'1', (byte)';', (byte)('0' + m), c };
            }
            else {
                return new byte[] { 0x1b, (byte)'O', c };
            }
        }

        private byte[] XtermFunctionKeyF5ToF12(int m, byte c1, byte c2) {
            if (m > 1) {
                return new byte[] { 0x1b, (byte)'[', c1, c2, (byte)';', (byte)('0' + m), (byte)'~' };
            }
            else {
                return new byte[] { 0x1b, (byte)'[', c1, c2, (byte)'~' };
            }
        }

        // emulate Xterm's modifyCursorKeys
        private byte[] ModifyCursorKey(Keys modifier, Keys key) {
            char c;
            switch (key) {
                case Keys.Up:
                    c = 'A';
                    break;
                case Keys.Down:
                    c = 'B';
                    break;
                case Keys.Right:
                    c = 'C';
                    break;
                case Keys.Left:
                    c = 'D';
                    break;
                default:
                    return null;
            }

            int m = 1;
            if ((modifier & Keys.Shift) != Keys.None) {
                m += 1;
            }
            if ((modifier & Keys.Alt) != Keys.None) {
                m += 2;
            }
            if ((modifier & Keys.Control) != Keys.None) {
                m += 4;
            }
            if (m == 1 || m == 8) {
                return null;
            }

            switch (XTermPreferences.Instance.modifyCursorKeys) {
                // only modifyCursorKeys=2 and modifyCursorKeys=3 are supported
                case 2: {
                        byte[] data = new byte[] {
                            0x1b, (byte)'[', (byte)'1', (byte)';', (byte)('0' + m), (byte)c
                        };
                        return data;
                    }
                case 3: {
                        byte[] data = new byte[] {
                            0x1b, (byte)'[', (byte)'>', (byte)'1', (byte)';', (byte)('0' + m), (byte)c
                        };
                        return data;
                    }
            }

            return null;
        }

        public override void FullReset() {
            InitTabStops();
            base.FullReset();
        }

        private ProcessCharResult ProcessDECSETMulti(string param, char code) {
            if (param.Length == 0)
                return ProcessCharResult.Processed;
            bool question = param[0] == '?';
            string[] ps = question ? param.Substring(1).Split(';') : param.Split(';');
            bool unsupported = false;
            foreach (string p in ps) {
                ProcessCharResult r = question ? ProcessDECSET(p, code) : ProcessSetMode(p, code);
                if (r == ProcessCharResult.Unsupported)
                    unsupported = true;
            }
            return unsupported ? ProcessCharResult.Unsupported : ProcessCharResult.Processed;
        }

        protected virtual ProcessCharResult ProcessSetMode(string param, char code) {
            bool set = code == 'h';
            switch (param) {
                case "4":
                    _insertMode = set; //hで始まってlで終わる
                    return ProcessCharResult.Processed;
                case "12":	//local echo
                    _afterExitLockActions.Add(new LocalEchoChanger(GetTerminalSettings(), !set).Do);
                    return ProcessCharResult.Processed;
                case "20":
                    return ProcessCharResult.Processed; //!!WinXPのTelnetで確認した
                case "25":
                    return ProcessCharResult.Processed;
                case "34":	//MakeCursorBig, puttyにはある
                    //!setでカーソルを強制的に箱型にし、setで通常に戻すというのが正しい動作だが実害はないので無視
                    return ProcessCharResult.Processed;
                default:
                    return ProcessCharResult.Unsupported;
            }
        }

        private class LocalEchoChanger {
            private ITerminalSettings _settings;
            private bool _value;
            public LocalEchoChanger(ITerminalSettings settings, bool value) {
                _settings = settings;
                _value = value;
            }
            public void Do() {
                _settings.BeginUpdate();
                _settings.LocalEcho = _value;
                _settings.EndUpdate();
            }
        }

        //これを送ってくるアプリケーションは viで上方スクロール
        protected void ProcessInsertLines(string param) {
            int d = ParseInt(param, 1);

            TerminalDocument doc = GetDocument();
            int caret_pos = _manipulator.CaretColumn;
            int offset = doc.CurrentLineNumber - doc.TopLineNumber;
            doc.UpdateCurrentLine(_manipulator);
            if (doc.ScrollingBottom == -1)
                doc.SetScrollingRegion(0, GetDocument().TerminalHeight - 1);

            for (int i = 0; i < d; i++) {
                doc.ScrollUp(doc.CurrentLineNumber, doc.ScrollingBottom);
                doc.CurrentLineNumber = doc.TopLineNumber + offset;
            }
            _manipulator.Load(doc.CurrentLine, caret_pos);
        }

        //これを送ってくるアプリケーションは viで下方スクロール
        protected void ProcessDeleteLines(string param) {
            int d = ParseInt(param, 1);

            /*
            TerminalDocument doc = GetDocument();
            _manipulator.Clear(GetConnection().TerminalWidth);
            GLine target = doc.CurrentLine;
            for(int i=0; i<d; i++) {
                target.Clear();
                target = target.NextLine;
            }
            */

            TerminalDocument doc = GetDocument();
            int caret_col = _manipulator.CaretColumn;
            int offset = doc.CurrentLineNumber - doc.TopLineNumber;
            doc.UpdateCurrentLine(_manipulator);
            if (doc.ScrollingBottom == -1)
                doc.SetScrollingRegion(0, doc.TerminalHeight - 1);

            for (int i = 0; i < d; i++) {
                doc.ScrollDown(doc.CurrentLineNumber, doc.ScrollingBottom);
                doc.CurrentLineNumber = doc.TopLineNumber + offset;
            }
            _manipulator.Load(doc.CurrentLine, caret_col);
        }

        //FormatExceptionのほかにOverflowExceptionの可能性もあるので
        protected static int ParseInt(string param, int default_value) {
            try {
                if (param.Length > 0)
                    return Int32.Parse(param);
                else
                    return default_value;
            }
            catch (Exception ex) {
                throw new UnknownEscapeSequenceException(String.Format("bad number format [{0}] : {1}", param, ex.Message));
            }
        }

        protected static IntPair ParseIntPair(string param, int default_first, int default_second) {
            IntPair ret = new IntPair(default_first, default_second);

            string[] s = param.Split(';');

            if (s.Length >= 1 && s[0].Length > 0) {
                try {
                    ret.first = Int32.Parse(s[0]);
                }
                catch (Exception ex) {
                    throw new UnknownEscapeSequenceException(String.Format("bad number format [{0}] : {1}", s[0], ex.Message));
                }
            }

            if (s.Length >= 2 && s[1].Length > 0) {
                try {
                    ret.second = Int32.Parse(s[1]);
                }
                catch (Exception ex) {
                    throw new UnknownEscapeSequenceException(String.Format("bad number format [{0}] : {1}", s[1], ex.Message));
                }
            }

            return ret;
        }

        //ModalTaskのセットを見る
        public override void StartModalTerminalTask(IModalTerminalTask task) {
            base.StartModalTerminalTask(task);
            _currentCharacterTask = (IModalCharacterTask)task.GetAdapter(typeof(IModalCharacterTask));
        }
        public override void EndModalTerminalTask() {
            base.EndModalTerminalTask();
            _currentCharacterTask = null;
        }

        //動的変更用
        private class CaptionChanger {
            private ITerminalSettings _settings;
            private string _title;
            public CaptionChanger(ITerminalSettings settings, string title) {
                _settings = settings;
                _title = title;
            }
            public void Do() {
                _settings.BeginUpdate();
                _settings.Caption = _title;
                _settings.EndUpdate();
            }
        }

        #region ScreenBufferManager

        /// <summary>
        /// Management of the screen buffer emulation.
        /// </summary>
        private class ScreenBufferManager {

            private readonly XTerm _term;

            private readonly List<GLine>[] _savedScreen = new List<GLine>[2];	// { main, alternate }
            private bool _isAlternateBuffer = false;
            private bool _saved_isAlternateBuffer = false;
            private readonly int[] _xtermSavedRow = new int[2];	// { main, alternate }
            private readonly int[] _xtermSavedCol = new int[2];	// { main, alternate }

            public ScreenBufferManager(XTerm term) {
                _term = term;
            }

            /// <summary>
            /// Saves current buffer mode.
            /// </summary>
            public void SaveBufferMode() {
                _saved_isAlternateBuffer = _isAlternateBuffer;
            }

            /// <summary>
            /// Restores buffer mode.
            /// </summary>
            public void RestoreBufferMode() {
                SwitchBuffer(_saved_isAlternateBuffer);
            }

            /// <summary>
            /// Switches buffer.
            /// </summary>
            /// <param name="toAlternate">true if the alternate buffer is to be switched to.</param>
            public void SwitchBuffer(bool toAlternate) {
                if (_isAlternateBuffer != toAlternate) {
                    SaveScreen(toAlternate ? 0 : 1);
                    RestoreScreen(toAlternate ? 1 : 0);
                    _isAlternateBuffer = toAlternate;
                }
            }

            /// <summary>
            /// Saves current cursor position.
            /// </summary>
            public void SaveCursor() {
                int sw = _isAlternateBuffer ? 1 : 0;
                TerminalDocument doc = _term.GetDocument();
                _xtermSavedRow[sw] = doc.CurrentLineNumber - doc.TopLineNumber;
                _xtermSavedCol[sw] = _term._manipulator.CaretColumn;
            }

            /// <summary>
            /// Restores cursor position.
            /// </summary>
            public void RestoreCursor() {
                int sw = _isAlternateBuffer ? 1 : 0;
                TerminalDocument doc = _term.GetDocument();
                doc.UpdateCurrentLine(_term._manipulator);
                doc.CurrentLineNumber = doc.TopLineNumber + _xtermSavedRow[sw];
                _term._manipulator.Load(doc.CurrentLine, _xtermSavedCol[sw]);
            }

            private void SaveScreen(int sw) {
                List<GLine> lines = new List<GLine>();
                TerminalDocument doc = _term.GetDocument();
                GLine l = doc.TopLine;
                int m = l.ID + doc.TerminalHeight;
                while (l != null && l.ID < m) {
                    lines.Add(l.Clone());
                    l = l.NextLine;
                }
                _savedScreen[sw] = lines;
            }

            private void RestoreScreen(int sw) {
                if (_savedScreen[sw] == null) {
                    _term.ClearScreen();	// emulate new buffer
                    return;
                }
                TerminalDocument doc = _term.GetDocument();
                int w = doc.TerminalWidth;
                int m = doc.TerminalHeight;
                GLine t = doc.TopLine;
                foreach (GLine l in _savedScreen[sw]) {
                    l.ExpandBuffer(w);
                    if (t == null)
                        doc.AddLine(l);
                    else {
                        doc.Replace(t, l);
                        t = l.NextLine;
                    }
                    if (--m == 0)
                        break;
                }
            }
        }

        #endregion

        #region MouseTrackingManager

        /// <summary>
        /// Management of the mouse tracking.
        /// </summary>
        private class MouseTrackingManager {

            public enum MouseTrackingState {
                Off,
                Normal,
                Drag,
                Any,
            }

            public enum MouseTrackingProtocol {
                Normal,
                Utf8,
                Urxvt,
                Sgr,
            }

            private readonly XTerm _term;

            private MouseTrackingState _mouseTrackingState = MouseTrackingState.Off;
            private MouseTrackingProtocol _mouseTrackingProtocol = MouseTrackingProtocol.Normal;
            private int _prevMouseRow = -1;
            private int _prevMouseCol = -1;
            private MouseButtons _mouseButton = MouseButtons.None;

            private const int MOUSE_POS_LIMIT = 255 - 32;       // mouse position limit
            private const int MOUSE_POS_EXT_LIMIT = 2047 - 32;  // mouse position limit in extended mode
            private const int MOUSE_POS_EXT_START = 127 - 32;   // mouse position to start using extended format

            public MouseTrackingManager(XTerm term) {
                _term = term;
            }

            /// <summary>
            /// Set mouse tracking state.
            /// </summary>
            /// <param name="newState">new state</param>
            public void SetMouseTrackingState(MouseTrackingState newState) {
                if (_mouseTrackingState == newState) {
                    return;
                }

                _mouseTrackingState = newState;

                if (newState == MouseTrackingManager.MouseTrackingState.Off) {
                    _term.ResetDocumentCursor();
                }
                else {
                    _term.SetDocumentCursor(Cursors.Arrow);
                }
            }

            /// <summary>
            /// Set mouse tracking protocol.
            /// </summary>
            /// <param name="newProtocol">new protocol</param>
            public void SetMouseTrackingProtocol(MouseTrackingProtocol newProtocol) {
                _mouseTrackingProtocol = newProtocol;
            }

            /// <summary>
            /// Hande mouse action.
            /// </summary>
            /// <param name="action">Action type</param>
            /// <param name="button">Which mouse button caused the event</param>
            /// <param name="modKeys">Modifier keys (Shift, Ctrl or Alt) being pressed</param>
            /// <param name="row">Row index (zero based)</param>
            /// <param name="col">Column index (zero based)</param>
            public bool ProcessMouse(TerminalMouseAction action, MouseButtons button, Keys modKeys, int row, int col) {

                MouseTrackingState currentState = _mouseTrackingState;  // copy value because _mouseTrackingState may be changed in non-UI thread.

                if (currentState == MouseTrackingState.Off) {
                    _prevMouseRow = -1;
                    _prevMouseCol = -1;
                    switch (action) {
                        case TerminalMouseAction.ButtonUp:
                        case TerminalMouseAction.ButtonDown:
                            _mouseButton = MouseButtons.None;
                            break;
                    }
                    return false;
                }

                // Note: from here, mouse event is consumed even if nothing has been processed actually.

                MouseTrackingProtocol protocol = _mouseTrackingProtocol; // copy value because _mouseTrackingProtocol may be changed in non-UI thread.

                int posLimit = protocol == MouseTrackingProtocol.Normal ? MOUSE_POS_LIMIT : MOUSE_POS_EXT_LIMIT;

                if (row < 0)
                    row = 0;
                else if (row > posLimit)
                    row = posLimit;

                if (col < 0)
                    col = 0;
                else if (col > posLimit)
                    col = posLimit;

                int statBits;
                switch (action) {
                    case TerminalMouseAction.ButtonDown:
                        if (_mouseButton != MouseButtons.None) {
                            return true;    // another button is already pressed
                        }

                        switch (button) {
                            case MouseButtons.Left:
                                statBits = 0x00;
                                break;
                            case MouseButtons.Middle:
                                statBits = 0x01;
                                break;
                            case MouseButtons.Right:
                                statBits = 0x02;
                                break;
                            default:
                                return true;    // unsupported button
                        }

                        _mouseButton = button;
                        break;

                    case TerminalMouseAction.ButtonUp:
                        if (button != _mouseButton) {
                            return true;    // ignore
                        }

                        if (protocol == MouseTrackingProtocol.Sgr) {
                            switch (button) {
                                case MouseButtons.Left:
                                    statBits = 0x00;
                                    break;
                                case MouseButtons.Middle:
                                    statBits = 0x01;
                                    break;
                                case MouseButtons.Right:
                                    statBits = 0x02;
                                    break;
                                default:
                                    return true;    // unsupported button
                            }
                        }
                        else {
                            statBits = 0x03;
                        }

                        _mouseButton = MouseButtons.None;
                        break;

                    case TerminalMouseAction.WheelUp:
                        statBits = 0x40;
                        break;

                    case TerminalMouseAction.WheelDown:
                        statBits = 0x41;
                        break;

                    case TerminalMouseAction.MouseMove:
                        if (currentState != MouseTrackingState.Any && currentState != MouseTrackingState.Drag) {
                            return true;    // no need to send
                        }

                        if (currentState == MouseTrackingState.Drag && _mouseButton == MouseButtons.None) {
                            return true;    // no need to send
                        }

                        if (row == _prevMouseRow && col == _prevMouseCol) {
                            return true;    // no need to send
                        }

                        switch (_mouseButton) {
                            case MouseButtons.Left:
                                statBits = 0x20;
                                break;
                            case MouseButtons.Middle:
                                statBits = 0x21;
                                break;
                            case MouseButtons.Right:
                                statBits = 0x22;
                                break;
                            default:
                                statBits = 0x20;
                                break;
                        }
                        break;

                    default:
                        return true;    // unknown action
                }

                if ((modKeys & Keys.Shift) != Keys.None) {
                    statBits |= 0x04;
                }

                if ((modKeys & Keys.Alt) != Keys.None) {
                    statBits |= 0x08;   // Meta key
                }

                if ((modKeys & Keys.Control) != Keys.None) {
                    statBits |= 0x10;
                }

                if (protocol != MouseTrackingProtocol.Sgr) {
                    statBits += 0x20;
                }

                _prevMouseRow = row;
                _prevMouseCol = col;

                byte[] data;
                int dataLen;

                switch (protocol) {

                    case MouseTrackingProtocol.Normal: {
                            data = new byte[] {
                                (byte)27, // ESCAPE
                                (byte)91, // [
                                (byte)77, // M
                                (byte)statBits,
                                (col == posLimit) ?
                                    (byte)0 :                   // emulate xterm's bug
                                    (byte)(col + (1 + 0x20)),   // column 0 --> send as 1
                                (row == posLimit) ?
                                    (byte)0 :                   // emulate xterm's bug
                                    (byte)(row + (1 + 0x20)),   // row 0 --> send as 1
                            };
                            dataLen = data.Length;
                        }
                        break;

                    case MouseTrackingProtocol.Utf8: {
                            data = new byte[8] {
                                (byte)27, // ESCAPE
                                (byte)91, // [
                                (byte)77, // M
                                (byte)statBits,
                                0,0,0,0,
                            };

                            dataLen = 4;

                            if (col < MOUSE_POS_EXT_START) {
                                data[dataLen++] = (byte)(col + (1 + 0x20));     // column 0 --> send as 1
                            }
                            else { // encode in UTF-8
                                int val = col + 1 + 0x20;
                                data[dataLen++] = (byte)(0xc0 + (val >> 6));
                                data[dataLen++] = (byte)(0x80 + (val & 0x3f));
                            }

                            if (row < MOUSE_POS_EXT_START) {
                                data[dataLen++] = (byte)(row + (1 + 0x20));     // row 0 --> send as 1
                            }
                            else { // encode in UTF-8
                                int val = row + (1 + 0x20);
                                data[dataLen++] = (byte)(0xc0 + (val >> 6));
                                data[dataLen++] = (byte)(0x80 + (val & 0x3f));
                            }
                        }
                        break;

                    case MouseTrackingProtocol.Urxvt: {
                            data = Encoding.ASCII.GetBytes(
                                new StringBuilder()
                                    .Append("\x1b[")
                                    .Append(statBits.ToString(NumberFormatInfo.InvariantInfo))
                                    .Append(';')
                                    .Append((col + 1).ToString(NumberFormatInfo.InvariantInfo))
                                    .Append(';')
                                    .Append((row + 1).ToString(NumberFormatInfo.InvariantInfo))
                                    .Append("M")
                                    .ToString());
                            dataLen = data.Length;
                        }
                        break;

                    case MouseTrackingProtocol.Sgr: {
                            data = Encoding.ASCII.GetBytes(
                                new StringBuilder()
                                    .Append("\x1b[<")
                                    .Append(statBits.ToString(NumberFormatInfo.InvariantInfo))
                                    .Append(';')
                                    .Append((col + 1).ToString(NumberFormatInfo.InvariantInfo))
                                    .Append(';')
                                    .Append((row + 1).ToString(NumberFormatInfo.InvariantInfo))
                                    .Append(action == TerminalMouseAction.ButtonUp ? 'm' : 'M')
                                    .ToString());
                            dataLen = data.Length;
                        }
                        break;

                    default:
                        return true;    // unknown protocol
                }

                _term.TransmitDirect(data, 0, dataLen);

                return true;
            }
        }

        #endregion

        #region FocusReportingManager

        /// <summary>
        /// Management of the focus reporting.
        /// </summary>
        private class FocusReportingManager {

            private readonly XTerm _term;

            private bool _focusReportingMode = false;

            private readonly byte[] _gotFocusBytes = new byte[] { 0x1b, 0x5b, 0x49 };
            private readonly byte[] _lostFocusBytes = new byte[] { 0x1b, 0x5b, 0x4f };

            public FocusReportingManager(XTerm term) {
                _term = term;
            }

            /// <summary>
            /// Sets focus reporting mode.
            /// </summary>
            /// <param name="sw">true if the focus reporting is enabled.</param>
            public void SetFocusReportingMode(bool sw) {
                _focusReportingMode = sw;
            }

            /// <summary>
            /// Handles got-focus event.
            /// </summary>
            public void OnGotFocus() {
                if (_focusReportingMode) {
                    _term.TransmitDirect(_gotFocusBytes, 0, _gotFocusBytes.Length);
                }
            }

            /// <summary>
            /// Handles lost-focus event.
            /// </summary>
            public void OnLostFocus() {
                if (_focusReportingMode) {
                    _term.TransmitDirect(_lostFocusBytes, 0, _lostFocusBytes.Length);
                }
            }
        }

        #endregion

        #region BracketedPasteModeManager

        /// <summary>
        /// Management of the bracketed paste mode.
        /// </summary>
        private class BracketedPasteModeManager {

            private readonly XTerm _term;

            private bool _bracketedPasteMode = false;

            private readonly byte[] _bracketedPasteModeLeadingBytes = new byte[] { 0x1b, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~' };
            private readonly byte[] _bracketedPasteModeTrailingBytes = new byte[] { 0x1b, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };
            private readonly byte[] _bracketedPasteModeEmptyBytes = new byte[0];

            public BracketedPasteModeManager(XTerm term) {
                _term = term;
            }

            /// <summary>
            /// Sets the bracketed paste mode.
            /// </summary>
            /// <param name="sw">true if the bracketed paste mode is enabled.</param>
            public void SetBracketedPasteMode(bool sw) {
                _bracketedPasteMode = sw;
            }

            /// <summary>
            /// Gets leading bytes for the pasted data.
            /// </summary>
            /// <returns>leading bytes</returns>
            public byte[] GetPasteLeadingBytes() {
                return _bracketedPasteMode ? _bracketedPasteModeLeadingBytes : _bracketedPasteModeEmptyBytes;
            }

            /// <summary>
            /// Gets trailing bytes for the pasted data.
            /// </summary>
            /// <returns>trailing bytes</returns>
            public byte[] GetPasteTrailingBytes() {
                return _bracketedPasteMode ? _bracketedPasteModeTrailingBytes : _bracketedPasteModeEmptyBytes;
            }
        }

        #endregion
    }

    /// <summary>
    /// Preferences for XTerm
    /// </summary>
    internal class XTermPreferences : IPreferenceSupplier {

        private static XTermPreferences _instance = new XTermPreferences();

        public static XTermPreferences Instance {
            get {
                return _instance;
            }
        }

        private const int DEFAULT_MODIFY_CURSOR_KEYS = 2;

        private IIntPreferenceItem _modifyCursorKeys;

        /// <summary>
        /// Xterm's modifyCursorKeys feature
        /// </summary>
        public int modifyCursorKeys {
            get {
                if (_modifyCursorKeys != null)
                    return _modifyCursorKeys.Value;
                else
                    return DEFAULT_MODIFY_CURSOR_KEYS;
            }
        }

        #region IPreferenceSupplier

        public string PreferenceID {
            get {
                return TerminalEmulatorPlugin.PLUGIN_ID + ".xterm";
            }
        }

        public void InitializePreference(IPreferenceBuilder builder, IPreferenceFolder folder) {
            _modifyCursorKeys = builder.DefineIntValue(folder, "modifyCursorKeys", DEFAULT_MODIFY_CURSOR_KEYS, PreferenceValidatorUtil.PositiveIntegerValidator);
        }

        public object QueryAdapter(IPreferenceFolder folder, Type type) {
            return null;
        }

        public void ValidateFolder(IPreferenceFolder folder, IPreferenceValidationResult output) {
        }

        #endregion
    }
}
