using System.Reflection;
using Module.Camoprof.Features.AddProfile;
using Xunit;

namespace Module.Camoprof.Tests;

/// <summary>
/// The compiler-level security boundary: the types that leave the Add
/// Profile feature toward Launcher/UI must not even be able to carry a
/// secret. If someone adds a Password-like property to these records,
/// this test fails before any review has to catch it. Also guards the
/// one-way Launcher contract: ExecuteAsync's shape must not grow an
/// escape hatch.
/// </summary>
public class AddProfileContractTests
{
    private static readonly string[] SecretNameFragments =
    {
        "password",
        "secret",
        "credential",
        "passwd",
        "token",
    };

    public static IEnumerable<object[]> UiVisibleTypes()
    {
        yield return new object[] { typeof(AddProfileResult) };
        yield return new object[] { typeof(AddProfileUpdate) };
        yield return new object[] { typeof(AddProfileOutcome) };
    }

    [Theory]
    [MemberData(nameof(UiVisibleTypes))]
    public void Ui_visible_types_carry_no_secret_properties(Type type)
    {
        // A bool outcome flag (e.g. SavedCredential) cannot carry a
        // secret; only value-carrying property types are dangerous.
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.PropertyType != typeof(bool))
            .Where(property => SecretNameFragments.Any(fragment =>
                property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            properties.Count == 0,
            type.Name + " exposes secret-shaped properties: "
            + string.Join(", ", properties.Select(property => property.Name)));
    }

    [Fact]
    public void AddProfileResult_fields_are_non_secret()
    {
        var fields = typeof(AddProfileResult)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => SecretNameFragments.Any(fragment =>
                field.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            fields.Count == 0,
            "AddProfileResult carries secret-shaped fields: "
            + string.Join(", ", fields.Select(field => field.Name)));
    }

    [Fact]
    public void Feature_contract_shape_is_secret_free_and_one_way()
    {
        // ExecuteAsync must stay
        // (AddProfileRequest, IProgress, CancellationToken)
        //     -> Task<AddProfileResult>: no out/ref parameters a
        // password could hide in, and the request is a plain record.
        var method = typeof(AddProfileFeature).GetMethod(
            "ExecuteAsync",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<AddProfileResult>), method.ReturnType);
        Assert.All(
            method.GetParameters(),
            parameter => Assert.True(
                !parameter.ParameterType.IsByRef,
                parameter.Name + " must not be by-ref"));
    }
}
