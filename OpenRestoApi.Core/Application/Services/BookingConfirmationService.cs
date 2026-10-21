using Microsoft.Extensions.Logging;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Services;

/// <inheritdoc cref="IBookingConfirmationService" />
public sealed class BookingConfirmationService(
    EmailSettingsService? emailSettingsService,
    IEmailService? emailService,
    IEmailFailureRepository? emailFailureRepository,
    IEmailTemplateService templateService,
    BrandService brandService,
    ILogger<BookingConfirmationService>? logger = null) : IBookingConfirmationService
{
    private readonly ILogger<BookingConfirmationService>? _logger = logger;
    private readonly EmailSettingsService? _emailSettingsService = emailSettingsService;
    private readonly IEmailService? _emailService = emailService;
    private readonly IEmailFailureRepository? _emailFailureRepository = emailFailureRepository;
    private readonly IEmailTemplateService _templateService = templateService;
    private readonly BrandService _brandService = brandService;

    public async Task SendConfirmationAsync(Booking booking, Restaurant restaurant)
    {
        // Same null-guard chain as the original BookingService block: short-circuits when any
        // prerequisite is missing so booking creation is never blocked by email configuration.
        if (_emailSettingsService == null || _emailService == null || string.IsNullOrEmpty(booking.CustomerEmail))
        {
            return;
        }

        try
        {
            var settings = await _emailSettingsService.GetAsync();
            if (settings?.SendBookingConfirmations != true)
            {
                return;
            }

            var brand = await _brandService.GetAsync();
            string websiteUrl = _brandService.GetWebsiteUrl(brand);
            string subject = _templateService.BuildConfirmationSubject(restaurant);
            string body = _templateService.BuildConfirmationEmail(booking, restaurant, brand, websiteUrl);
            await _emailService.SendEmailAsync(booking.CustomerEmail, subject, body);
        }
        catch (Exception ex)
        {
            // The full exception stays in the log; the table keeps only the category.
            _logger?.LogError(ex, "Confirmation email failed for booking {BookingRef}.", booking.BookingRef);
            if (_emailFailureRepository != null)
            {
                await _emailFailureRepository.AddAsync(new EmailFailure
                {
                    BookingRef = booking.BookingRef,
                    RecipientEmail = booking.CustomerEmail,
                    ErrorMessage = EmailFailureReason.CodeFor(ex),
                    AttemptedAt = DateTime.UtcNow,
                });
            }
        }
    }
}
