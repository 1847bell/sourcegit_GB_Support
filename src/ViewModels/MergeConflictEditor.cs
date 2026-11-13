using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class MergeConflictEditor : ObservableObject
    {
        public string FilePath
        {
            get => _filePath;
        }

        public object Mine
        {
            get;
        }

        public object Theirs
        {
            get;
        }

        public string Error
        {
            get => _error;
            private set => SetProperty(ref _error, value);
        }

        public List<Models.ConflictLine> OursLines
        {
            get => _oursLines;
            private set => SetProperty(ref _oursLines, value);
        }

        public List<Models.ConflictLine> TheirsLines
        {
            get => _theirsLines;
            private set => SetProperty(ref _theirsLines, value);
        }

        public List<Models.ConflictLine> ResultLines
        {
            get => _resultLines;
            private set => SetProperty(ref _resultLines, value);
        }

        public int MaxLineNumber
        {
            get => _maxLineNumber;
            private set => SetProperty(ref _maxLineNumber, value);
        }

        public int UnsolvedCount
        {
            get => _unsolvedCount;
            private set => SetProperty(ref _unsolvedCount, value);
        }

        public Vector ScrollOffset
        {
            get => _scrollOffset;
            set => SetProperty(ref _scrollOffset, value);
        }

        public Models.ConflictSelectedChunk SelectedChunk
        {
            get => _selectedChunk;
            set => SetProperty(ref _selectedChunk, value);
        }

        public IReadOnlyList<Models.ConflictRegion> ConflictRegions
        {
            get => _conflictRegions;
        }

        public Models.TextEncoding SelectedEncoding
        {
            get => _selectedEncoding;
            set
            {
                if (value == null || value == _selectedEncoding)
                    return;

                if (LoadContent(value))
                    SetProperty(ref _selectedEncoding, value);
                else
                    OnPropertyChanged(nameof(SelectedEncoding));
            }
        }

        public string UsedEncoding
        {
            get => _usedEncoding;
            private set => SetProperty(ref _usedEncoding, value);
        }

        public MergeConflictEditor(Repository repo, Models.Commit head, string filePath)
        {
            _repo = repo;
            _filePath = filePath;

            (Mine, Theirs) = repo.InProgressContext switch
            {
                CherryPickInProgress cherryPick => (head, cherryPick.Head),
                RebaseInProgress rebase => (rebase.Onto, rebase.StoppedAt),
                AmInProgress applying => (head, (object)"Patch"),
                RevertInProgress revert => (head, revert.Head),
                MergeInProgress merge => (head, merge.Source),
                _ => (head, (object)"Stash or Patch"),
            };

            var workingCopyPath = Path.Combine(_repo.FullPath, _filePath);
            if (File.Exists(workingCopyPath))
                _originalBytes = File.ReadAllBytes(workingCopyPath);

            LoadContent(_selectedEncoding);
        }

        public Models.ConflictLineState GetLineState(int line)
        {
            if (line >= 0 && line < _lineStates.Count)
                return _lineStates[line];
            return Models.ConflictLineState.Normal;
        }

        public void Resolve(object param)
        {
            if (_selectedChunk == null)
                return;

            var region = _conflictRegions[_selectedChunk.ConflictIndex];
            if (param is not Models.ConflictResolution resolution)
                return;

            // Try to resolve a resolved region.
            if (resolution != Models.ConflictResolution.None && region.IsResolved)
                return;

            // Try to undo an unresolved region.
            if (resolution == Models.ConflictResolution.None && !region.IsResolved)
                return;

            region.IsResolved = resolution != Models.ConflictResolution.None;
            region.ResolutionType = resolution;
            RefreshDisplayData();
        }

        public async Task<bool> SaveAndStageAsync()
        {
            if (_conflictRegions.Count == 0)
                return true;

            if (_unsolvedCount > 0)
            {
                Error = "Cannot save: there are still unresolved conflicts.";
                return false;
            }

            var lines = _originalContent.Split('\n', StringSplitOptions.None);
            var builder = new StringBuilder();
            var lastLineIdx = 0;

            foreach (var r in _conflictRegions)
            {
                for (var i = lastLineIdx; i < r.StartLineInOriginal; i++)
                    builder.Append(lines[i]).Append('\n');

                if (r.ResolutionType == Models.ConflictResolution.UseOurs)
                {
                    foreach (var l in r.OursContent)
                        builder.Append(l).Append('\n');
                }
                else if (r.ResolutionType == Models.ConflictResolution.UseTheirs)
                {
                    foreach (var l in r.TheirsContent)
                        builder.Append(l).Append('\n');
                }
                else if (r.ResolutionType == Models.ConflictResolution.UseBothMineFirst)
                {
                    foreach (var l in r.OursContent)
                        builder.Append(l).Append('\n');

                    foreach (var l in r.TheirsContent)
                        builder.Append(l).Append('\n');
                }
                else if (r.ResolutionType == Models.ConflictResolution.UseBothTheirsFirst)
                {
                    foreach (var l in r.TheirsContent)
                        builder.Append(l).Append('\n');

                    foreach (var l in r.OursContent)
                        builder.Append(l).Append('\n');
                }

                lastLineIdx = r.EndLineInOriginal + 1;
            }

            for (var j = lastLineIdx; j < lines.Length; j++)
                builder.Append(lines[j]).Append('\n');

            if (builder.Length > 1)
                builder.Length--; // Remove extra '\n'

            try
            {
                // Write merged content to file
                var fullPath = Path.Combine(_repo.FullPath, _filePath);
                await File.WriteAllTextAsync(fullPath, builder.ToString(), _encoding);

                // Stage the file
                var pathSpecFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(pathSpecFile, _filePath);
                await new Commands.Add(_repo.FullPath, pathSpecFile).ExecAsync();
                File.Delete(pathSpecFile);

                _repo.MarkWorkingCopyDirtyManually();
                return true;
            }
            catch (Exception ex)
            {
                Error = $"Failed to save and stage: {ex.Message}";
                return false;
            }
        }

        public void ClearErrorMessage()
        {
            Error = string.Empty;
        }

        private bool LoadContent(Models.TextEncoding selectedEncoding)
        {
            var encoding = selectedEncoding.Decoder;
            if (selectedEncoding.IsAuto && Preferences.Instance.EnableEncodingDetection)
            {
                var detector = new Models.EncodingDetector();
                detector.Feed(_originalBytes);
                encoding = detector.GetResult().Decoder;
            }

            using var reader = new StreamReader(new MemoryStream(_originalBytes, false), encoding, selectedEncoding.IsAuto);
            var content = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;

            if (content.IndexOf('\0', StringComparison.Ordinal) >= 0)
            {
                Error = "Binary file is not supported.";
                return false;
            }

            // Preserve the original UTF-8 BOM instead of adding one to BOM-less files.
            if (encoding.CodePage == Encoding.UTF8.CodePage)
                encoding = new UTF8Encoding(_originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));

            var previousRegions = _conflictRegions;
            ParseOriginalContent(content);

            // Changing an ASCII-compatible encoding does not change conflict marker positions.
            for (var i = 0; i < _conflictRegions.Count && i < previousRegions.Count; i++)
            {
                var current = _conflictRegions[i];
                var previous = previousRegions[i];
                if (current.StartLineInOriginal == previous.StartLineInOriginal &&
                    current.EndLineInOriginal == previous.EndLineInOriginal)
                {
                    current.IsResolved = previous.IsResolved;
                    current.ResolutionType = previous.ResolutionType;
                }
            }

            _encoding = encoding;
            UsedEncoding = Models.TextEncoding.Find(encoding.CodePage)?.Name ?? encoding.WebName;
            Error = string.Empty;
            RefreshDisplayData();
            return true;
        }

        private void ParseOriginalContent(string content)
        {
            _originalContent = content;
            _conflictRegions = [];

            if (string.IsNullOrEmpty(content))
                return;

            var lines = content.Split('\n', StringSplitOptions.None);
            var oursLines = new List<Models.ConflictLine>();
            var theirsLines = new List<Models.ConflictLine>();
            int oursLineNumber = 1;
            int theirsLineNumber = 1;
            int i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];

                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
                {
                    var region = new Models.ConflictRegion
                    {
                        StartLineInOriginal = i,
                        StartMarker = line,
                    };

                    oursLines.Add(new());
                    theirsLines.Add(new());
                    i++;

                    // Collect ours content
                    while (i < lines.Length &&
                           !lines[i].StartsWith("|||||||", StringComparison.Ordinal) &&
                           !lines[i].StartsWith("=======", StringComparison.Ordinal))
                    {
                        line = lines[i];
                        region.OursContent.Add(line);
                        oursLines.Add(new(Models.ConflictLineType.Ours, line, oursLineNumber++));
                        theirsLines.Add(new());
                        i++;
                    }

                    // Skip diff3 base section if present
                    if (i < lines.Length && lines[i].StartsWith("|||||||", StringComparison.Ordinal))
                    {
                        i++;
                        while (i < lines.Length && !lines[i].StartsWith("=======", StringComparison.Ordinal))
                            i++;
                    }

                    // Capture separator marker
                    if (i < lines.Length && lines[i].StartsWith("=======", StringComparison.Ordinal))
                    {
                        oursLines.Add(new());
                        theirsLines.Add(new());
                        region.SeparatorMarker = lines[i];
                        i++;
                    }

                    // Collect theirs content
                    while (i < lines.Length && !lines[i].StartsWith(">>>>>>>", StringComparison.Ordinal))
                    {
                        line = lines[i];
                        region.TheirsContent.Add(line);
                        oursLines.Add(new());
                        theirsLines.Add(new(Models.ConflictLineType.Theirs, line, theirsLineNumber++));
                        i++;
                    }

                    // Capture end marker (e.g., ">>>>>>> feature-branch")
                    if (i < lines.Length && lines[i].StartsWith(">>>>>>>", StringComparison.Ordinal))
                    {
                        oursLines.Add(new());
                        theirsLines.Add(new());

                        region.EndMarker = lines[i];
                        region.EndLineInOriginal = i;
                        i++;
                    }

                    _conflictRegions.Add(region);
                }
                else
                {
                    oursLines.Add(new(Models.ConflictLineType.Common, line, oursLineNumber));
                    theirsLines.Add(new(Models.ConflictLineType.Common, line, theirsLineNumber));
                    i++;
                    oursLineNumber++;
                    theirsLineNumber++;
                }
            }

            MaxLineNumber = Math.Max(oursLineNumber, theirsLineNumber);
            OursLines = oursLines;
            TheirsLines = theirsLines;
        }

        private void RefreshDisplayData()
        {
            var resultLines = new List<Models.ConflictLine>();
            _lineStates.Clear();

            if (_oursLines == null || _oursLines.Count == 0)
            {
                ResultLines = resultLines;
                return;
            }

            int resultLineNumber = 1;
            int currentLine = 0;
            int conflictIdx = 0;

            while (currentLine < _oursLines.Count)
            {
                // Check if we're at a conflict region
                Models.ConflictRegion currentRegion = null;
                if (conflictIdx < _conflictRegions.Count)
                {
                    var region = _conflictRegions[conflictIdx];
                    if (region.StartLineInOriginal == currentLine)
                        currentRegion = region;
                }

                if (currentRegion != null)
                {
                    int regionLines = currentRegion.EndLineInOriginal - currentRegion.StartLineInOriginal + 1;
                    if (currentRegion.IsResolved)
                    {
                        var oldLineCount = resultLines.Count;
                        var resolveType = currentRegion.ResolutionType;

                        // Resolved - show resolved content with color based on resolution type
                        if (resolveType == Models.ConflictResolution.UseBothMineFirst)
                        {
                            int mineCount = currentRegion.OursContent.Count;
                            for (int i = 0; i < mineCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Ours, currentRegion.OursContent[i], resultLineNumber));
                                resultLineNumber++;
                            }

                            int theirsCount = currentRegion.TheirsContent.Count;
                            for (int i = 0; i < theirsCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Theirs, currentRegion.TheirsContent[i], resultLineNumber));
                                resultLineNumber++;
                            }
                        }
                        else if (resolveType == Models.ConflictResolution.UseBothTheirsFirst)
                        {
                            int theirsCount = currentRegion.TheirsContent.Count;
                            for (int i = 0; i < theirsCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Theirs, currentRegion.TheirsContent[i], resultLineNumber));
                                resultLineNumber++;
                            }

                            int mineCount = currentRegion.OursContent.Count;
                            for (int i = 0; i < mineCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Ours, currentRegion.OursContent[i], resultLineNumber));
                                resultLineNumber++;
                            }
                        }
                        else if (resolveType == Models.ConflictResolution.UseOurs)
                        {
                            int mineCount = currentRegion.OursContent.Count;
                            for (int i = 0; i < mineCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Ours, currentRegion.OursContent[i], resultLineNumber));
                                resultLineNumber++;
                            }
                        }
                        else if (resolveType == Models.ConflictResolution.UseTheirs)
                        {
                            int theirsCount = currentRegion.TheirsContent.Count;
                            for (int i = 0; i < theirsCount; i++)
                            {
                                resultLines.Add(new(Models.ConflictLineType.Theirs, currentRegion.TheirsContent[i], resultLineNumber));
                                resultLineNumber++;
                            }
                        }

                        // Pad with empty lines to match Mine/Theirs panel height
                        int added = resultLines.Count - oldLineCount;
                        int padding = regionLines - added;
                        for (int p = 0; p < padding; p++)
                            resultLines.Add(new());

                        int blockSize = resultLines.Count - oldLineCount - 2;
                        _lineStates.Add(Models.ConflictLineState.ResolvedBlockStart);
                        for (var i = 0; i < blockSize; i++)
                            _lineStates.Add(Models.ConflictLineState.ResolvedBlock);
                        _lineStates.Add(Models.ConflictLineState.ResolvedBlockEnd);
                    }
                    else
                    {
                        resultLines.Add(new(Models.ConflictLineType.Marker, currentRegion.StartMarker));
                        _lineStates.Add(Models.ConflictLineState.ConflictBlockStart);

                        foreach (var line in currentRegion.OursContent)
                        {
                            resultLines.Add(new(Models.ConflictLineType.Ours, line, resultLineNumber++));
                            _lineStates.Add(Models.ConflictLineState.ConflictBlock);
                        }

                        resultLines.Add(new(Models.ConflictLineType.Marker, currentRegion.SeparatorMarker));
                        _lineStates.Add(Models.ConflictLineState.ConflictBlock);

                        foreach (var line in currentRegion.TheirsContent)
                        {
                            resultLines.Add(new(Models.ConflictLineType.Theirs, line, resultLineNumber++));
                            _lineStates.Add(Models.ConflictLineState.ConflictBlock);
                        }

                        resultLines.Add(new(Models.ConflictLineType.Marker, currentRegion.EndMarker));
                        _lineStates.Add(Models.ConflictLineState.ConflictBlockEnd);
                    }

                    currentLine = currentRegion.EndLineInOriginal + 1;
                    conflictIdx++;
                }
                else
                {
                    var oursLine = _oursLines[currentLine];
                    resultLines.Add(new(oursLine.Type, oursLine.Content, resultLineNumber));
                    _lineStates.Add(Models.ConflictLineState.Normal);
                    resultLineNumber++;
                    currentLine++;
                }
            }

            SelectedChunk = null;
            ResultLines = resultLines;

            var unsolved = new List<int>();
            for (var i = 0; i < _conflictRegions.Count; i++)
            {
                var r = _conflictRegions[i];
                if (!r.IsResolved)
                    unsolved.Add(i);
            }

            UnsolvedCount = unsolved.Count;
        }

        private readonly Repository _repo;
        private readonly string _filePath;
        private readonly byte[] _originalBytes = [];
        private Encoding _encoding = new UTF8Encoding(false);
        private Models.TextEncoding _selectedEncoding = Models.TextEncoding.Auto;
        private string _usedEncoding = string.Empty;
        private string _originalContent = string.Empty;
        private int _unsolvedCount = 0;
        private int _maxLineNumber = 0;
        private List<Models.ConflictLine> _oursLines = [];
        private List<Models.ConflictLine> _theirsLines = [];
        private List<Models.ConflictLine> _resultLines = [];
        private List<Models.ConflictRegion> _conflictRegions = [];
        private List<Models.ConflictLineState> _lineStates = [];
        private Vector _scrollOffset = Vector.Zero;
        private Models.ConflictSelectedChunk _selectedChunk;
        private string _error = string.Empty;
    }
}
