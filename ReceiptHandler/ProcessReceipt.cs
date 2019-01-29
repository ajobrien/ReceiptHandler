using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;

namespace ReceiptHandler
{
    public static class ProcessReceipt
    {

        // Replace <Subscription Key> with your valid subscription key.
        private const string SubscriptionKey = "<OCR Subscription Key";

        // You must use the same Azure region in your REST API method as you used to
        // get your subscription keys. For example, if you got your subscription keys
        // from the West US region, replace "australiaeast" in the URL
        // below with "westus".
        //
        // Free trial subscription keys are generated in the "westus" region.
        // If you use a free trial subscription key, you shouldn't need to change
        // this region.
        private const string UriBase = "https://australiaeast.api.cognitive.microsoft.com/vision/v2.0/ocr";

        // The url we will access the uploaded receipts from.
        private const string blobUriBase = "https://vtpowerstorage.blob.core.windows.net/receipts/";

        // Key words to search receipt for
        private static readonly Dictionary<string, string> KeyWords = new Dictionary<string, string>()
        {
            {"Taxi", "Cabs/Uber"},
            {"Cab", "Cabs/Uber" },
            {"Office", "Stationary" },
            {"Stationary", "Stationary" }
        };

        [FunctionName("ProcessReceipt")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            // these lines extract a file bytes from the form-data in the request body.
            var data = await req.Content.ReadAsMultipartAsync();
            var contents = await data.Contents[0].ReadAsByteArrayAsync();

            // Resize contents if required.
            var image = ResizeImage(ref contents);

            // OCR Image
            var ocrResult = await MakeOcrRequest(contents);
            if (ocrResult == "failed")
                return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Could not process receipt.");

            var jToken = JToken.Parse(ocrResult);

            var orientation = jToken.Value<string>("orientation");
            if (orientation != null && orientation != "Up") { 
                image = RotateImage(image, orientation);
                ImageToByteArray(image);
            }

            // Store Image in Azure.
            var storeResult = StoreImage(contents);
            if (storeResult == "failed")
                return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Could not save receipt.");

            // Regex to located money values enclosed in "" e.g: "$200.05"
            var regex = new Regex("\\\"\\$[0-9]+(\\.[0-9]{1,2})?\\\"");
            var costs = new List<string>();

            // Find all money values
            foreach (Match match in regex.Matches(ocrResult))
            {
                costs.Add(match.Value.Trim('"'));
            }

            // check if a key word exists.
            var expenseType = "";
            foreach (var key in KeyWords.Keys)
            {
                if (ocrResult.IndexOf(key, StringComparison.InvariantCultureIgnoreCase) > 0)
                {
                    expenseType = KeyWords[key];
                    break;
                }
            }

            return req.CreateResponse(HttpStatusCode.OK, new {
                costs,
                url = storeResult,
                expenseType
            });
        }

        /// <summary>
        /// Resize image if greater than 3200x3200 pixels, which is the max size OCR will accept 
        /// </summary>
        /// <param name="contents"></param>
        static Image ResizeImage(ref byte[] contents)
        {
            using (MemoryStream ms = new MemoryStream(contents))
            {
                var image = Image.FromStream(ms);
                var size = image.Size;

                if (size.Height > 3200 || size.Width > 3200)
                {
                    image = size.Height > size.Width
                        ? new Bitmap(image, (int) (size.Width * ((decimal) 3200 / size.Height)), 3200)
                        : new Bitmap(image, 3200, (int) (size.Height * ((decimal) 3200 / size.Width)));
                    contents = ImageToByteArray(image);
                }

                return image;
            }
        }

        /// <summary>
        /// Convert Image to byte[]
        /// </summary>
        /// <param name="image"></param>
        /// <returns></returns>
        static byte[] ImageToByteArray(Image image)
        {
            using (var ms = new MemoryStream())
            {
                image.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Gets the text visible in the specified image file by using
        /// the Computer Vision REST API.
        /// </summary>
        /// <param name="contents">Image Data to OCR</param>
        static async Task<string> MakeOcrRequest(byte[] contents)
        {
            try
            {
                HttpClient client = new HttpClient();

                // Request headers.
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);

                // Request parameters. 
                // The language parameter doesn't specify a language, so the 
                // method detects it automatically.
                // The detectOrientation parameter is set to true, so the method detects and
                // and corrects text orientation before detecting text.
                string requestParameters = "language=unk&detectOrientation=true";

                // Assemble the URI for the REST API method.
                string uri = UriBase + "?" + requestParameters;

                HttpResponseMessage response;

                // Add the byte array as an octet stream to the request body.
                using (ByteArrayContent content = new ByteArrayContent(contents))
                {
                    // This example uses the "application/octet-stream" content type.
                    // The other content types you can use are "application/json"
                    // and "multipart/form-data".
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");

                    // Asynchronously call the REST API method.
                    response = await client.PostAsync(uri, content);
                }

                // Asynchronously get the JSON response.
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine("\n" + e.Message);
            }

            return "failed";
        }

        /// <summary>
        /// method to rotate an image either clockwise or counter-clockwise
        /// </summary>
        /// <param name="img">the image to be rotated</param>
        /// <param name="currentDirection">Left, Down, Right</param>
        /// <returns></returns>
        public static Image RotateImage(Image img, string currentDirection)
        {
            //create an empty Bitmap image
            Bitmap bmp = new Bitmap(img.Width, img.Height);

            //turn the Bitmap into a Graphics object
            Graphics gfx = Graphics.FromImage(bmp);

            //now we set the rotation point to the center of our image
            gfx.TranslateTransform((float)bmp.Width / 2, (float)bmp.Height / 2);

            switch (currentDirection)
            {
                case "Left":
                    gfx.RotateTransform(90);
                    break;
                case "Down":
                    gfx.RotateTransform(180);
                    break;
                case "Right":
                    gfx.RotateTransform(270);
                    break;
            }

            //now rotate the image
            //gfx.RotateTransform(rotationAngle);

            gfx.TranslateTransform(-(float)bmp.Width / 2, -(float)bmp.Height / 2);

            //set the InterpolationMode to HighQualityBicubic so to ensure a high
            //quality image once it is transformed to the specified size
            gfx.InterpolationMode = InterpolationMode.HighQualityBicubic;

            //now draw our new image onto the graphics object
            gfx.DrawImage(img, new Point(0, 0));

            //dispose of our Graphics object
            gfx.Dispose();

            //return the image
            return bmp;
        }

        /// <summary>
        /// Store image in Azure
        /// </summary>
        /// <param name="contents"></param>
        /// <returns></returns>
        static string StoreImage(byte[] contents)
        {
            // Retrieve the connection string for use with the application. The storage connection string is stored
            // in an environment variable on the machine running the application called storageconnectionstring.
            // If the environment variable is created after the application is launched in a console or with Visual
            // Studio, the shell or application needs to be closed and reloaded to take the environment variable into account.
            var storageConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

            // Check whether the connection string can be parsed.
            if (!CloudStorageAccount.TryParse(storageConnectionString, out var storageAccount)) return "failed";

            // Create the CloudBlobClient that represents the Blob storage endpoint for the storage account.
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();

            // Reference to the receipts container.
            var cloudBlobContainer = cloudBlobClient.GetContainerReference("receipts");

            // Create the Blob.
            var fileName = Guid.NewGuid() + ".jpg";
            var cloudBlockBlob = cloudBlobContainer.GetBlockBlobReference(fileName);
            cloudBlockBlob.UploadFromByteArray(contents, 0, contents.Length);

            return blobUriBase + fileName;

        }
    }

}
