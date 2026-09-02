using System.Reflection;
using Module.Camoprof.Providers.Google.Enrollment;
using Xunit;

namespace Module.Camoprof.Tests;

/// <summary>
/// The compiler-level security boundary: the types that leave the
/// Enrollment folder toward Launcher/UI must not even be able to carry
/// a secret. If someone adds a Password-like property to these records,
/// this test fails before any review has to catch it.
/// </summary>
public class GoogleEnrollmentContractTests
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
        yield return new object[] { typeof(GoogleEnrollmentResult) };
        yield return new object[] { typeof(GoogleEnrollmentUpdate) };
        yield return new object[] { typeof(GoogleEnrollmentOutcome) };
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
    public void GoogleEnrollmentResult_fields_are_non_secret()
    {
        var fields = typeof(GoogleEnrollmentResult)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => SecretNameFragments.Any(fragment =>
                field.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            fields.Count == 0,
            "GoogleEnrollmentResult carries secret-shaped fields: "
            + string.Join(", ", fields.Select(field => field.Name)));
    }

    [Fact]
    public void Enrollment_feature_contract_is_secret_free_by_shape()
    {
        // EnrollAsync's signature must stay (string, string?, IProgress,
        // CancellationToken) -> Task<GoogleEnrollmentResult>: no out/ref,
        // no object-returning escape hatch a password could hide in.
        var method = typeof(GoogleEnrollmentFeature).GetMethod(
            "EnrollAsync",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<GoogleEnrollmentResult>), method.ReturnType);
        Assert.All(
            method.GetParameters(),
            parameter => Assert.True(
                !parameter.ParameterType.IsByRef,
                parameter.Name + " must not be by-ref"));
    }
}
