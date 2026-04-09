using Application.Common.Exceptions;
using Application.Common.Exceptions.ExceptionTypes;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace Application.Common.Tests.Exceptions;

public class ExceptionTypesTests
{
    [Fact]
    public void BadRequestException_DefaultConstructor_HasDefaultMessage()
    {
        var ex = new BadRequestException();

        ex.Message.Should().Be("The request was invalid or could not be processed.");
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }

    [Fact]
    public void BadRequestException_WithMessage_StoresMessage()
    {
        var ex = new BadRequestException("Invalid input.");

        ex.Message.Should().Be("Invalid input.");
    }

    [Fact]
    public void BadRequestException_WithInnerException_ChainsCorrectly()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new BadRequestException("outer", inner);

        ex.Message.Should().Be("outer");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void ForbiddenAccessException_DefaultConstructor_HasDefaultMessage()
    {
        var ex = new ForbiddenAccessException();

        ex.Message.Should().Be("User is not authorized to perform this action.");
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }

    [Fact]
    public void EntityNotFoundException_WithEntityAndKey_FormatsMessage()
    {
        var ex = new EntityNotFoundException("User", 42);

        ex.Message.Should().Be("Entity \"User\" (42) was not found.");
        ex.EntityName.Should().Be("User");
        ex.Key.Should().Be(42);
    }

    [Fact]
    public void DatabaseInteractionException_WithOperationContext_FormatsMessage()
    {
        var inner = new InvalidOperationException("db error");
        var ex = new DatabaseInteractionException("Update", "Product", 99, inner);

        ex.Message.Should().Be("There was an issue performing a 'Update' for entity 'Product' with key '99'.");
        ex.Operation.Should().Be("Update");
        ex.EntityName.Should().Be("Product");
        ex.Key.Should().Be(99);
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void DatabaseInteractionException_WithoutKey_FormatsMessageWithoutKey()
    {
        var ex = new DatabaseInteractionException("Create", "Order");

        ex.Message.Should().Be("There was an issue performing a 'Create' for entity 'Order'.");
        ex.Key.Should().BeNull();
    }

    [Fact]
    public void ConfigurationNotFoundException_WithSectionAndKey_FormatsMessage()
    {
        var ex = new ConfigurationNotFoundException("Database", "ConnectionString");

        ex.Message.Should().Be("Configuration section 'Database' with key 'ConnectionString' was not found.");
        ex.Section.Should().Be("Database");
        ex.Key.Should().Be("ConnectionString");
    }

    [Fact]
    public void ConfigurationNotFoundException_ForSection_SetsSectionProperty()
    {
        var ex = ConfigurationNotFoundException.ForSection("AI");

        ex.Message.Should().Be("Configuration section 'AI' was not found.");
        ex.Section.Should().Be("AI");
        ex.Key.Should().BeNull();
    }

    [Fact]
    public void ValidationException_WithFailures_GroupsByPropertyName()
    {
        var failures = new List<ValidationFailure>
        {
            new("Email", "Email is required"),
            new("Email", "Email format is invalid"),
            new("Name", "Name is required")
        };

        var ex = new ValidationException(failures);

        ex.Errors.Should().ContainKey("Email");
        ex.Errors["Email"].Should().BeEquivalentTo("Email is required", "Email format is invalid");
        ex.Errors["Name"].Should().BeEquivalentTo("Name is required");
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }

    [Fact]
    public void ValidationException_DefaultConstructor_HasEmptyErrors()
    {
        var ex = new ValidationException();

        ex.Errors.Should().BeEmpty();
        ex.Message.Should().Be("One or more validation failures have occurred.");
    }

    [Fact]
    public void NoContentException_DefaultConstructor_HasDefaultMessage()
    {
        var ex = new NoContentException();

        ex.Message.Should().Be("The operation completed successfully but returned no content.");
        ex.Should().BeAssignableTo<ApplicationExceptionBase>();
    }

    [Fact]
    public void AllExceptionTypes_InheritFromApplicationExceptionBase()
    {
        new BadRequestException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new ForbiddenAccessException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new NoContentException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new EntityNotFoundException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new DatabaseInteractionException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new ConfigurationNotFoundException().Should().BeAssignableTo<ApplicationExceptionBase>();
        new ValidationException().Should().BeAssignableTo<ApplicationExceptionBase>();
    }
}
