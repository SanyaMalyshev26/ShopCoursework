using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ShopCoursework.Services.Images;

public sealed record ProcessedImage(string ImageUrl, string ThumbnailUrl, int Width, int Height);

public sealed class ImageProcessingException(
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public interface IImageProcessingClient
{
    Task<ProcessedImage> ProcessAsync(IFormFile image, CancellationToken cancellationToken);
}

public sealed class ImageProcessingClient(HttpClient httpClient) : IImageProcessingClient
{
    public async Task<ProcessedImage> ProcessAsync(IFormFile image, CancellationToken cancellationToken)
    {
        if (httpClient.BaseAddress == null)
            throw new ImageProcessingException("Image processing service is not configured.");

        using var content = new MultipartFormDataContent();
        await using var imageStream = image.OpenReadStream();
        using var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        content.Add(imageContent, "image", "upload");

        using var response = await httpClient.PostAsync("images", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ImageProcessingException(
                "Image processing service rejected the upload.",
                response.StatusCode);

        ProcessedImage? processedImage;
        try
        {
            processedImage = await response.Content.ReadFromJsonAsync<ProcessedImage>(
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImageProcessingException(
                "Image processing service returned invalid JSON.",
                innerException: exception);
        }
        if (processedImage == null ||
            !IsUploadPath(processedImage.ImageUrl) ||
            !IsUploadPath(processedImage.ThumbnailUrl) ||
            processedImage.Width is < 1 or > 1600 ||
            processedImage.Height is < 1 or > 1600)
        {
            throw new ImageProcessingException("Image processing service returned an invalid response.");
        }

        return processedImage;
    }

    private static bool IsUploadPath(string? path) =>
        path != null &&
        path.StartsWith("/uploads/", StringComparison.Ordinal) &&
        path.Length > "/uploads/".Length &&
        path["/uploads/".Length..].All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
