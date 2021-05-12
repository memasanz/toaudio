
using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.CognitiveServices.Speech;



namespace create_audio_output
{
    public static class Function1
    {
        private static readonly string BLOB_STORAGE_CONNECTION_STRING = Environment.GetEnvironmentVariable("BLOB_STORAGE_CONNECTION_STRING");
        private static readonly string AUDIO_OUTPUT_CONTAINER_NAME = Environment.GetEnvironmentVariable("AUDIO_OUTPUT_CONTAINER_NAME");
        private static readonly string TEXT_CONTAINER_NAME = Environment.GetEnvironmentVariable("TEXT_CONTAINER_NAME");
        private static readonly bool USE_SDK = Convert.ToBoolean(Environment.GetEnvironmentVariable("USE_SDK"));
        private static readonly string OCP_APIM_SUBSCRIPTION_KEY = Environment.GetEnvironmentVariable("OCP_APIM_SUBSCRIPTION_KEY");
        private static readonly string SERVICE_REGION = Environment.GetEnvironmentVariable("SERVICE_REGION");
        private static readonly string VOICE = Environment.GetEnvironmentVariable("VOICE");
        private static HttpClient _client = new HttpClient();
        private static DateTime TokenExpire =    DateTime.MinValue;
        private static string BearerToken = "";


        [FunctionName("create_audio_output")]
        public static async Task RunAsync([EventGridTrigger] EventGridEvent eventGridEvent,  ILogger log)

      {
            log.LogInformation("data = " + eventGridEvent.Data.ToString());
            log.LogInformation("topic = " + eventGridEvent.Topic.ToString());
            log.LogInformation("Subject = " + eventGridEvent.Subject.ToString());

            try
            {
             
                var extension = Path.GetExtension(eventGridEvent.Topic);
                var sourcefileName = Path.GetFileName(eventGridEvent.Subject);
                var destfileName = Path.GetFileNameWithoutExtension(eventGridEvent.Subject) + ".mp3";
                var ext = Path.GetExtension(sourcefileName);

                log.LogInformation("extension = " + ext);
                log.LogInformation("sourcefileName: " + sourcefileName);
                log.LogInformation("dest file name " + destfileName);

                var textdata = await GetBlobContext(sourcefileName, log);

                if (USE_SDK)
                    await SynthesizeAudioAsync(textdata, destfileName, log);               
                else
                    await GetAudioViaRestAPI(textdata, destfileName, log);


            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                throw;
            }



        }

        private static async Task GetAudioViaRestAPI(string textdata, string destfileName, ILogger log)
        {
            var audioFilebytes = await TextToAudio(textdata, log);
            var blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(AUDIO_OUTPUT_CONTAINER_NAME);



            using (var ms = new MemoryStream(audioFilebytes))
            {
                await blobContainerClient.UploadBlobAsync(destfileName, ms);
            }

        }

        private static async Task<string> GetBlobContext(string sourcefileName, ILogger log)
        {

            log.LogInformation(BLOB_STORAGE_CONNECTION_STRING);
            BlobServiceClient blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
            
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(TEXT_CONTAINER_NAME);
            log.LogInformation("container =" + TEXT_CONTAINER_NAME);
            BlobClient blobClient = containerClient.GetBlobClient(sourcefileName);
            if (await blobClient.ExistsAsync())
            {
                var response = await blobClient.DownloadAsync();
                using (var streamReader = new StreamReader(response.Value.Content))
                {
                    while (!streamReader.EndOfStream)
                    {
                        return (await streamReader.ReadLineAsync());
                        //Console.WriteLine(line);
                    }
                }
            }

                return null;

        }


        public static async Task<(string, DateTime)> GetAuthToken()
        {
            HttpRequestMessage requestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                "https://eastus.api.cognitive.microsoft.com/sts/v1.0/issuetoken");

            requestMessage.Headers.Add("Ocp-Apim-Subscription-Key",
                System.Environment.GetEnvironmentVariable("ApiKey",
                EnvironmentVariableTarget.Process));

            var token = await _client.SendAsync(requestMessage);

            return (await token.Content.ReadAsStringAsync(),
                DateTime.Now.AddMinutes(9));
        }

        public static async Task<byte[]> TextToAudio(string document, ILogger log)
        {
            if (DateTime.Now > TokenExpire)
            {
                var (token, expiration) = await GetAuthToken();

                BearerToken = "Bearer " + token;
                TokenExpire = expiration;
            }

            var audioRequestBody = GetAudioRequest(document);

            log.LogInformation("requestBody =" + audioRequestBody);

            HttpRequestMessage audioRequest =new HttpRequestMessage(HttpMethod.Post,"https://eastus.tts.speech.microsoft.com/cognitiveservices/v1");

            audioRequest.Content = new StringContent(audioRequestBody);
            audioRequest.Content.Headers.ContentType =MediaTypeHeaderValue.Parse("application/ssml+xml");
            audioRequest.Headers.Authorization =AuthenticationHeaderValue.Parse(BearerToken);
            audioRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("PluralsightFunction", "V1.0"));
            audioRequest.Headers.Add("X-Microsoft-OutputFormat","audio-24khz-48kbitrate-mono-mp3");

            var audioResult = await _client.SendAsync(audioRequest);

            return await audioResult.Content.ReadAsByteArrayAsync();
        }

        private static string GetAudioRequest(string document)
        {
            var ssml = "<speak version='1.0' xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang='en-US'>" +
                        "<voice  name='Microsoft Server Speech Text to Speech Voice (en-US, Jessa24kRUS)'>" +
                        "{{ReplaceText}}" +
                        "</voice> </speak>";

            return ssml.Replace("{{ReplaceText}}", document);
        }
        static async Task SynthesizeAudioAsync(string textdata, string destfileName, ILogger log)
        {
            var config = SpeechConfig.FromSubscription(OCP_APIM_SUBSCRIPTION_KEY, SERVICE_REGION);
            config.SpeechSynthesisVoiceName = VOICE; //"en-US-AriaNeural";
            config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm);

            using var synthesizer = new SpeechSynthesizer(config, null);
            var result = await synthesizer.SpeakTextAsync(textdata);
            
            using var stream = AudioDataStream.FromResult(result);

            string tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".wav");
            await stream.SaveToWaveFileAsync(tempFile);

            var blobServiceClient = new BlobServiceClient(BLOB_STORAGE_CONNECTION_STRING);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(AUDIO_OUTPUT_CONTAINER_NAME);

            byte[] bytes = System.IO.File.ReadAllBytes(tempFile);

            destfileName = Path.GetFileNameWithoutExtension(destfileName) + ".wav";

            using (var ms = new MemoryStream(bytes))
            {
                try
                {
                    await blobContainerClient.UploadBlobAsync(destfileName, ms);
                }
                catch
                {
                    log.LogInformation("overwrite existing blob");
                    await blobContainerClient.DeleteBlobIfExistsAsync(destfileName, Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots);
                    await blobContainerClient.UploadBlobAsync(destfileName, ms);
                }
                
            }
            File.Delete(tempFile);
        }

    }
}
