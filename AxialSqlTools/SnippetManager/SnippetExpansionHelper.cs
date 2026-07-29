using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Runtime.InteropServices;

namespace AxialSqlTools
{
    public static class SnippetExpansionHelper
    {
        public static bool TryExpandWordAtCaret(IVsTextView textView)
        {
            if (textView == null) return false;
            if (!TryGetWordSpanAtCaret(textView, out int line, out int wordStart, out int wordEnd, out string word))
                return false;
            if (string.IsNullOrEmpty(word)) return false;

            var dict = SnippetService.SnippetDictionary;
            if (!dict.TryGetValue(word, out SnippetItem snippet))
                return false;

            return TryExpandInView(textView, line, wordStart, wordEnd, snippet);
        }

        public static bool TryExpandInView(IVsTextView textView, int line, int wordStart, int wordEnd, SnippetItem snippet)
        {
            if (textView == null || snippet == null) return false;
            if (textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                return false;

            var settings = SettingsManager.GetSnippetSettings();
            var result = SnippetVariableProcessor.ProcessVariables(snippet.Body, settings.cursorMarker);
            string newText = result.ProcessedText ?? string.Empty;
            int cursorOffset = result.CursorOffset;

            if (wordStart > 0)
            {
                newText = newText.Replace(Environment.NewLine, Environment.NewLine + new string(' ', wordStart));
            }

            IntPtr pNewText = Marshal.StringToHGlobalUni(newText);
            try
            {
                TextSpan[] pChangedSpan = new TextSpan[1];
                textLines.ReplaceLines(line, wordStart, line, wordEnd, pNewText, newText.Length, pChangedSpan);
            }
            finally
            {
                Marshal.FreeHGlobal(pNewText);
            }

            SetCaretPosition(textView, line, wordStart, newText, cursorOffset >= 0 ? cursorOffset : newText.Length);
            return true;
        }

        public static bool TryGetWordSpanAtCaret(IVsTextView textView, out int line, out int wordStart, out int wordEnd, out string word)
        {
            line = 0;
            wordStart = 0;
            wordEnd = 0;
            word = string.Empty;

            if (textView.GetBuffer(out IVsTextLines textLines) != VSConstants.S_OK)
                return false;

            textView.GetCaretPos(out line, out int column);
            textLines.GetLengthOfLine(line, out int lineLength);
            textLines.GetLineText(line, 0, line, lineLength, out string lineText);

            if (string.IsNullOrEmpty(lineText) || column == 0)
                return false;

            wordStart = column;
            for (int i = column - 1; i >= 0; i--)
            {
                char c = lineText[i];
                if (c == ' ' || c == '\t' || c == '(' || c == ')' || c == ',' || c == ';')
                    break;
                wordStart = i;
            }

            if (wordStart >= column)
                return false;

            wordEnd = column;
            word = lineText.Substring(wordStart, wordEnd - wordStart).Trim();
            return !string.IsNullOrEmpty(word);
        }

        public static void SetCaretPosition(IVsTextView textView, int startLine, int startColumn, string text, int offset)
        {
            int targetLine = startLine;
            int targetColumn = startColumn;

            for (int i = 0; i < offset && i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                    i++;
                }
                else if (text[i] == '\n')
                {
                    targetLine++;
                    targetColumn = 0;
                }
                else
                {
                    targetColumn++;
                }
            }

            textView.SetCaretPos(targetLine, targetColumn);
        }
    }
}
