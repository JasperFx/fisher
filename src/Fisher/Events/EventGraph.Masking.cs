using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     The per-event-type rules that say how protected information is removed from an event body.
/// </summary>
/// <remarks>
///     <para>
///         Registered on the store's event options and applied by
///         <c>Advanced.ApplyEventDataMasking(...)</c>. JasperFx 2.41.0 lifted the *request* shape
///         (<see cref="JasperFx.Events.Protected.IEventDataMasking" />) into <c>JasperFx.Events</c>
///         because Marten's and Polecat's were identical, but the rule registry was **not** lifted —
///         it still lives in each store's own event graph, so this is a port of Polecat's rather than
///         a use of something shared.
///     </para>
///     <para>
///         Rules are matched by assignability, not by exact type, which is what lets one rule cover an
///         interface or base class that several event types share. Every rule is offered every event
///         and the results are OR'd, so two rules can both apply to one event.
///     </para>
/// </remarks>
public partial class EventGraph
{
    private readonly List<IMasker> _maskers = new();

    /// <summary>
    ///     Register a rule that mutates an event body of type <typeparamref name="T" /> in place.
    /// </summary>
    /// <remarks>
    ///     Use this for a class with settable members. A <c>record</c> that is rewritten with a
    ///     <c>with</c> expression needs the <see cref="AddMaskingRuleForProtectedInformation{T}(Func{T,T})" />
    ///     overload instead, since the mutation would otherwise be discarded.
    /// </remarks>
    public void AddMaskingRuleForProtectedInformation<T>(Action<T> masking) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(masking);
        _maskers.Add(new ActionMasker<T>(masking));
    }

    /// <summary>
    ///     Register a rule that replaces an event body of type <typeparamref name="T" /> with a new
    ///     instance — the shape a <c>record</c> needs.
    /// </summary>
    public void AddMaskingRuleForProtectedInformation<T>(Func<T, T> masking) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(masking);
        _maskers.Add(new FuncMasker<T>(masking));
    }

    /// <summary>
    ///     Whether any registered rule has anything to say about this event, applying every one that
    ///     does.
    /// </summary>
    internal bool TryMask(IEvent @event)
    {
        var matched = false;

        foreach (var masker in _maskers)
        {
            // Deliberately not short-circuiting: every matching rule has to run, so | rather than ||.
            matched |= masker.TryMask(@event);
        }

        return matched;
    }
}

internal interface IMasker
{
    bool TryMask(IEvent @event);
}

internal sealed class ActionMasker<T> : IMasker where T : notnull
{
    private readonly Action<T> _masking;

    internal ActionMasker(Action<T> masking)
    {
        _masking = masking;
    }

    public bool TryMask(IEvent @event)
    {
        // IEvent<T> rather than Event<T>: the interface is satisfied by any envelope carrying a body
        // assignable to T, which is what makes a rule registered against a base type or interface
        // cover every event that implements it.
        if (@event is not IEvent<T> typed)
        {
            return false;
        }

        _masking(typed.Data);
        return true;
    }
}

internal sealed class FuncMasker<T> : IMasker where T : notnull
{
    private readonly Func<T, T> _masking;

    internal FuncMasker(Func<T, T> masking)
    {
        _masking = masking;
    }

    public bool TryMask(IEvent @event)
    {
        // The closed envelope, because the replacement has to be assigned back and only Event<T>
        // exposes a setter. A rule registered against a base type therefore rewrites nothing here
        // even though the Action overload would have mutated it — which is why the two overloads
        // differ in reach, and why the Action one is the right choice for a hierarchy.
        if (@event is not Event<T> typed)
        {
            return false;
        }

        typed.Data = _masking(typed.Data);
        return true;
    }
}
