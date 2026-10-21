using Microsoft.AspNetCore.Mvc;
using Moq;
using OpenRestoApi.Controllers;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Controllers;

public class EmailSettingsControllerUnitTests
{
    private readonly Mock<EmailSettingsService> _mockService;
    private readonly EmailSettingsController _controller;

    public EmailSettingsControllerUnitTests()
    {
        _mockService = new Mock<EmailSettingsService>(null!, null!, null!, null!);
        _controller = new EmailSettingsController(_mockService.Object);
    }

    [Fact]
    public async Task Get_ReturnsEmptyResponse_WhenSettingsNull()
    {
        _mockService.Setup(s => s.GetAsync()).ReturnsAsync((EmailSettings?)null);
        var result = await _controller.Get();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<EmailSettingsResponse>(okResult.Value);
        Assert.False(resp.IsConfigured);
    }

    [Fact]
    public async Task Test_ReturnsBadRequest_WhenConnectionFails()
    {
        _mockService.Setup(s => s.TestConnectionAsync()).ThrowsAsync(new InvalidOperationException("Fail"));
        var result = await _controller.Test();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// What the SMTP stack says can name the host and the account, so the client gets a category
    /// code and a fixed sentence; a category the service already classified keeps its code.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.EmailAuthFailed, ErrorCodes.EmailAuthFailed)]
    [InlineData(ErrorCodes.EmailConnectionFailed, ErrorCodes.EmailConnectionFailed)]
    [InlineData(ErrorCodes.EmailSendFailed, ErrorCodes.EmailConnectionFailed)]
    public async Task Test_ReturnsAStableCode_AndNeverTheExceptionText(string thrownCode, string expectedCode)
    {
        const string Sensitive = "535 5.7.8 user bob@corp.example rejected by smtp.internal.example";
        _mockService.Setup(s => s.TestConnectionAsync())
            .ThrowsAsync(new InfrastructureException(Sensitive) { Code = thrownCode });

        var result = await _controller.Test();

        var response = Assert.IsType<MessageResponse>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal(expectedCode, response.Code);
        Assert.DoesNotContain("smtp.internal.example", response.Message);
        Assert.Null(response.Args);
    }

    [Fact]
    public async Task Test_AnUnclassifiedFailure_IsAConnectionFailure_WithoutItsText()
    {
        _mockService.Setup(s => s.TestConnectionAsync())
            .ThrowsAsync(new InvalidOperationException("secret detail at Foo.cs:42"));

        var result = await _controller.Test();

        var response = Assert.IsType<MessageResponse>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal(ErrorCodes.EmailConnectionFailed, response.Code);
        Assert.DoesNotContain("secret", response.Message);
    }

    [Fact]
    public async Task Test_WhenTheConnectionWorks_StillSucceeds()
    {
        _mockService.Setup(s => s.TestConnectionAsync()).ReturnsAsync(true);

        Assert.IsType<OkObjectResult>(await _controller.Test());
    }

    [Fact]
    public async Task Test_ReturnsBadRequest_WhenNotConfigured()
    {
        _mockService.Setup(s => s.TestConnectionAsync()).ReturnsAsync(false);
        var result = await _controller.Test();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Get_ReturnsSendBookingConfirmations_WhenSettingsExist()
    {
        _mockService.Setup(s => s.GetAsync()).ReturnsAsync(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user@test.com",
            EncryptedPassword = "enc",
            SendBookingConfirmations = true,
        });

        var result = await _controller.Get();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<EmailSettingsResponse>(okResult.Value);
        Assert.True(resp.SendBookingConfirmations);
        Assert.True(resp.IsConfigured);
    }

    [Fact]
    public async Task Get_ReturnsSendBookingConfirmationsFalse_ByDefault()
    {
        _mockService.Setup(s => s.GetAsync()).ReturnsAsync(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user@test.com",
            EncryptedPassword = "enc",
            SendBookingConfirmations = false,
        });

        var result = await _controller.Get();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<EmailSettingsResponse>(okResult.Value);
        Assert.False(resp.SendBookingConfirmations);
    }
}
