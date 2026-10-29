using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// The field-by-field half of the in-memory rule (#1005): an exemption whose reason rests on what a holder's records carry
/// is pinned to every field of those records and to every caller that fills them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the holder rule's own evidence is not enough.</strong> It pins the path from a holder to an owner field, such
/// as <c>ConnectionTracker._connections holds ActiveConversationInfo.UserId</c>. A field added beside that owner, a message
/// text or a tool result, changes none of it, so the exemption would go on excusing data its reason never looked at. Here
/// the reviewed types' every field is listed, and so is every production type that calls the holder's contract, because a
/// new caller can put something new into a field whose shape did not change (a progress event's free-text detail).
/// </para>
/// <para>
/// <strong>And the holders the rule cannot see.</strong> A store keyed by a run id, with no owner field on its records, is
/// invisible to the holder rule (its remarks state the blind spot). Such a type is listed by hand in
/// <see cref="HoldsOwnerRecordsWithoutStoringThem.UnseenByTheHolderRule"/>, with its reason, and must carry a field-by-field
/// review; when the rule starts seeing it, the entry moves to the rule's own table.
/// </para>
/// </remarks>
public sealed partial class EveryPersonalDataStoreIsDeclaredTests
{
    [Fact]
    public void EveryFieldByFieldReview_StillMatchesWhatTheReviewedTypesHoldAndWhoFillsThem()
    {
        var stale = HoldsOwnerRecordsWithoutStoringThem.ReviewedFieldByField
            .Select(r => (r.Key, r.Value, Seen: FieldByField(r.Value.Types)))
            .Where(r => !r.Seen.SetEquals(r.Value.Evidence))
            .Select(r => $"  {r.Key}:{Environment.NewLine}    " + string.Join(Environment.NewLine + "    ", r.Seen.Select(s => "\"" + s + "\",")))
            .ToList();

        stale.Should().BeEmpty(
            "a holder's exemption was written against these fields and callers; when one changes, re-read the type and what " +
            "its callers now put in it before copying the new evidence in" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void EveryFieldByFieldReview_IsOfAnExemptedHolder()
    {
        HoldsOwnerRecordsWithoutStoringThem.ReviewedFieldByField.Keys
            .Where(k => !HoldsOwnerRecordsWithoutStoringThem.Entries.ContainsKey(k)
                && !HoldsOwnerRecordsWithoutStoringThem.UnseenByTheHolderRule.ContainsKey(k))
            .Should().BeEmpty("a review pins the evidence behind an exemption, so it needs the exemption it supports");
    }

    [Fact]
    public void EveryHolderTheRuleCannotSee_IsStillUnseenUndeclaredAndReviewedFieldByField()
    {
        var holders = Holders.Value;
        foreach (var key in HoldsOwnerRecordsWithoutStoringThem.UnseenByTheHolderRule.Keys)
        {
            Scanned.Value.Facts.Should().ContainKey(key, "an exemption names a type that exists");
            holders.Should().NotContainKey(key, "the holder rule now sees it; move its entry to HoldsOwnerRecordsWithoutStoringThem.Entries");
            IsDeclared(Scanned.Value.Facts[key]).Should().BeFalse("a type is either described in the inventory or excused from it");
            HoldsOwnerRecordsWithoutStoringThem.ReviewedFieldByField.Should().ContainKey(key,
                "nothing else would notice what such a type starts holding");
        }
    }

    /// <summary>
    /// Every field of each reviewed class or record and the types nested in it, as <c>Type.Field: FieldType</c>, and, for a
    /// reviewed interface, every production type that calls it, as <c>Interface called by Caller</c>.
    /// </summary>
    private static SortedSet<string> FieldByField(IReadOnlyList<string> types)
    {
        var facts = Scanned.Value.Facts;
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in types)
        {
            if (!facts.TryGetValue(key, out var type))
            {
                lines.Add($"{key} is not a production type");
                continue;
            }

            if (type.IsInterface)
            {
                foreach (var caller in facts.Where(f => f.Value.Callees.Contains(key)).Select(f => f.Key))
                    lines.Add($"{type.Name} called by {caller}");
                continue;
            }

            foreach (var declared in type.Declared.Where(d => !d.IsCompilerGenerated))
                foreach (var field in declared.Fields)
                    lines.Add($"{declared.Name}.{field.Name}: {Render(field.Type)}");
        }

        return lines;
    }

    private static string Render(PersistenceScan.TypeShape type)
    {
        if (type.IsArray)
            return Render(type.Arguments[0]) + "[]";

        var name = type.Name.Split('`')[0];
        return type.Arguments.Count == 0 ? name : $"{name}<{string.Join(", ", type.Arguments.Select(Render))}>";
    }
}
