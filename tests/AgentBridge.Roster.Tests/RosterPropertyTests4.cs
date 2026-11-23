// A quarter of RosterPropertyTests' scenarios (see its Scenarios): the same two properties, so no one class is a shard's long pole.
public sealed class RosterPropertyTests4
{
    public static TheoryData<string> Scenarios => RosterPropertyTests.Group(3);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_delivery_order_reaches_the_same_state_and_the_same_verdicts(string scenario) => RosterPropertyTests.DeliveryOrders(scenario);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void An_entry_is_judged_in_the_state_of_its_ancestors_alone(string scenario) => RosterPropertyTests.AncestorsAlone(scenario);
}