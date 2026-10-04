using Fisher.Storage;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#384. In a Native AOT image the likeliest reason a document "has no identity member" is
///     that ILC trimmed it, so the message has to say so. Whether the write itself works natively is
///     <c>smoke/aot-consumer</c>'s job: nothing running under CoreCLR can observe it.
/// </summary>
public class native_aot_identity_message
{
    public class NoIdentity
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void in_a_native_image_the_message_names_the_trimming_cause_and_the_fix()
    {
        var message = DocumentMapping.DescribeMissingIdentity(typeof(NoIdentity), dynamicCodeSupported: false)
            .Message;

        message.ShouldContain("has no identity member");
        message.ShouldContain("Native AOT");
        message.ShouldContain("JsonSerializerContext");
        message.ShouldContain(nameof(StoreOptions.ConfigureSerialization));
    }

    [Fact]
    public void under_the_jit_the_message_is_unchanged()
    {
        var message = DocumentMapping.DescribeMissingIdentity(typeof(NoIdentity), dynamicCodeSupported: true)
            .Message;

        message.ShouldContain("has no identity member");
        message.ShouldNotContain("Native AOT");
    }
}
