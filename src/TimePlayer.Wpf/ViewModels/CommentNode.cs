using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimePlayer.Core.Models;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

public enum CommentNodeKind
{
    Comment,
    /// <summary>Stand-in child so a comment with replies shows as "collapsed" before they are loaded.</summary>
    Loading,
    /// <summary>"More replies": Enter loads the next page of replies in its place.</summary>
    MoreReplies
}

/// <summary>
/// One row of the comments tree. Top-level comments are level 1, their replies level 2;
/// screen readers announce the level and the expanded/collapsed state from the tree itself.
/// </summary>
public sealed partial class CommentNode : ObservableObject
{
    private readonly Action<CommentNode>? _expanded;

    private CommentNode(CommentNodeKind kind, Comment? comment, int level, Action<CommentNode>? expanded)
    {
        Kind = kind;
        Comment = comment;
        Level = level;
        _expanded = expanded;
    }

    public static CommentNode ForComment(Comment comment, int level, Action<CommentNode>? expanded)
    {
        var node = new CommentNode(CommentNodeKind.Comment, comment, level, expanded);
        if (comment.HasReplies && level == 1)
            node.Replies.Add(Loading(level + 1));
        return node;
    }

    public static CommentNode Loading(int level) => new(CommentNodeKind.Loading, null, level, null);

    public static CommentNode MoreReplies(Comment parent, string token, int level) =>
        new(CommentNodeKind.MoreReplies, parent, level, null) { MoreToken = token };

    public CommentNodeKind Kind { get; }
    public Comment? Comment { get; }
    public int Level { get; }
    public string? MoreToken { get; private init; }
    public ObservableCollection<CommentNode> Replies { get; } = new();
    public bool RepliesLoaded { get; set; }
    public bool IsComment => Kind == CommentNodeKind.Comment;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _expanded?.Invoke(this);
    }

    public string Author => Comment?.Author ?? string.Empty;
    public string Text => Kind switch
    {
        CommentNodeKind.Loading => T("Loading replies..."),
        CommentNodeKind.MoreReplies => T("More replies - press Enter"),
        _ => Comment?.Text ?? string.Empty
    };
    public string? Details => Kind == CommentNodeKind.Comment
        ? string.Join(" · ", new[] { Comment?.PublishedText, Comment?.LikesText }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public string AccessibleName => Kind switch
    {
        CommentNodeKind.Loading => T("Loading replies"),
        CommentNodeKind.MoreReplies => T("More replies, press Enter to load"),
        _ => Comment!.AccessibleSummary
    };

    public override string ToString() => AccessibleName;
}
