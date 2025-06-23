using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Api
{
   public class Upload
   {
       private readonly ILogger<Upload> _logger;
       private static readonly HttpClient _httpClient = new();

       public Upload(ILogger<Upload> logger)
       {
           _logger = logger;
       }

       [Function("upload")]
       public async Task<HttpResponseData> Run(
           [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequestData req)
       {
           _logger.LogInformation($"Upload function triggered with method: {req.Method}");

           var response = req.CreateResponse(HttpStatusCode.OK);
           response.Headers.Add("Access-Control-Allow-Origin", "*");
           response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
           response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

           if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
           {
               return response;
           }

           if (!req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
           {
               var notAllowed = req.CreateResponse(HttpStatusCode.MethodNotAllowed);
               notAllowed.Headers.Add("Access-Control-Allow-Origin", "*");
               return notAllowed;
           }

           string? fileName = null;
           string? contentType = null;
           byte[]? fileBytes = null;
           string? email = null;
           string? token = null;
           string? messageId   = null;
           string? uniqueFileName   = null;
           // Parse multipart/form-data
            if (req.Headers.TryGetValues("Content-Type", out var ctValues)
                && ctValues.FirstOrDefault() is string contentTypeHeader
                && contentTypeHeader.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var mediaType = MediaTypeHeaderValue.Parse(contentTypeHeader);
                var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
                if (string.IsNullOrWhiteSpace(boundary))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.WriteString("Missing multipart boundary.");
                    return bad;
                }

                var reader = new MultipartReader(boundary, req.Body);
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync()) != null)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                        continue;

                    if (disposition.IsFileDisposition() && disposition.FileName.HasValue)
                    {
                        fileName = disposition.FileName.Value.Trim('"');

                        // Try to get content type from section first, then fallback to file extension
                        contentType = section.ContentType;
                        if (string.IsNullOrWhiteSpace(contentType))
                        {
                            contentType = GetContentTypeFromFileName(fileName);
                        }

                        using var ms = new MemoryStream();
                        await section.Body.CopyToAsync(ms);
                        fileBytes = ms.ToArray();
                    }
                    else if (disposition.IsFormDisposition() && disposition.Name.HasValue)
                    {
                        var key = disposition.Name.Value.Trim('"');
                        using var sr = new StreamReader(section.Body);
                        var value = await sr.ReadToEndAsync();
                        if (key.Equals("email", StringComparison.OrdinalIgnoreCase))
                            email = value;
                        else if (key.Equals("token", StringComparison.OrdinalIgnoreCase))
                            token = value;
                        else if (key.Equals("messageid", StringComparison.OrdinalIgnoreCase))
                            messageId = value;
                        else if (key.Equals("uniqueFileName", StringComparison.OrdinalIgnoreCase))
                            uniqueFileName = value;
                    }
                }
            }
            else
            {
                // Fallback to JSON payload
                var json = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(json))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.WriteString("Empty request body.");
                    return bad;
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("fileName", out var fnProp))
                        fileName = fnProp.GetString();
                    if (root.TryGetProperty("contentType", out var ctProp))
                        contentType = ctProp.GetString();
                    if (root.TryGetProperty("fileBase64", out var fbProp) && fbProp.GetString() is string fb64)
                        fileBytes = Convert.FromBase64String(fb64);
                    if (root.TryGetProperty("email", out var eProp))
                        email = eProp.GetString();
                    if (root.TryGetProperty("token", out var tProp))
                        token = tProp.GetString();
                    if (root.TryGetProperty("messageId", out var midProp))
                        messageId = midProp.GetString();
                    if (root.TryGetProperty("uniqueFileName", out var ufnProp))
                        uniqueFileName = ufnProp.GetString();
                }
                catch (JsonException je)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.WriteString($"Invalid JSON: {je.Message}");
                    return bad;
                }
            }

           // Validate parsed data
           if (fileName == null || contentType == null || fileBytes == null || email == null || token == null || messageId == null || uniqueFileName == null)
           {
               var bad = req.CreateResponse(HttpStatusCode.BadRequest);
               bad.WriteString("Missing one or more required fields: fileName, contentType, file, email, token, messageId, uniqueFileName.");
               return bad;
           }

           _logger.LogInformation($"Parsed upload: fileName={fileName}, contentType={contentType}, email={email}" +
                                  $", token={(token.Length > 10 ? token.Substring(0, 10) + "..." : token)}, messageId={messageId}, uniqueFileName={uniqueFileName}, fileSize={fileBytes.Length} bytes");

           var flowPayload = new
           {
               fileName,
               contentType,
               fileBase64 = Convert.ToBase64String(fileBytes),
               email,
               token,
               messageId,
               uniqueFileName
           };
           var flowJson = JsonSerializer.Serialize(flowPayload);

           var flowUrl = Environment.GetEnvironmentVariable("FLOW_URL");
           if (!string.IsNullOrEmpty(flowUrl))
           {
               try
               {
                   var flowResp = await _httpClient.PostAsync(
                       flowUrl,
                       new StringContent(flowJson, Encoding.UTF8, "application/json")
                   );
                   _logger.LogInformation($"Flow called – Status: {flowResp.StatusCode}");
                   var flowContent = await flowResp.Content.ReadAsStringAsync();
                   _logger.LogInformation($"Flow response: {flowContent}");
               }
               catch (Exception ex)
               {
                   _logger.LogWarning(ex, "Error calling Flow");
               }
           }

           response.Headers.Add("Content-Type", "application/json");
           await response.WriteStringAsync(JsonSerializer.Serialize(new
           {
               status = "OK",
               success = true,
               message = "Upload processed successfully!"
           }));

           return response;
       }

       // Helper method to determine content type from file extension
       private static string GetContentTypeFromFileName(string fileName)
       {
           var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
           return extension switch
           {
               ".pdf" => "application/pdf",
               ".doc" => "application/msword",
               ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
               ".xls" => "application/vnd.ms-excel",
               ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
               ".txt" => "text/plain",
               ".jpg" or ".jpeg" => "image/jpeg",
               ".png" => "image/png",
               ".gif" => "image/gif",
               ".zip" => "application/zip",
               ".csv" => "text/csv",
               _ => "application/octet-stream"
           };
       }
   }
}