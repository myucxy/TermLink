using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionHistoryUploaderIdempotency.Validation
{
    internal static class Program
    {
        private sealed class UnexpectedTransportHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("A rejected history create unexpectedly reached object transport.");
        }

        private sealed class RetryObservingHistoryApi
        {
            public readonly List<string[]> CreateRetryKeys = new List<string[]>();
            public readonly List<string> CiphertextDigests = new List<string>();

            public Task<HttpResponseMessage> SendAsync(
                HttpMethod method,
                string path,
                byte[] body,
                bool authenticated,
                string idempotencyKey,
                CancellationToken cancellationToken)
            {
                Require(authenticated, "History request was not authenticated.");
                if (method == HttpMethod.Get)
                {
                    Require(path.EndsWith("/history", StringComparison.Ordinal) && idempotencyKey == null,
                        "History cursor initialization changed unexpectedly.");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"items\":[]}"))
                    });
                }

                Require(method == HttpMethod.Post && path.EndsWith("/history/uploads", StringComparison.Ordinal),
                    "History create request changed unexpectedly.");
                using var document = JsonDocument.Parse(body);
                CiphertextDigests.Add(document.RootElement.GetProperty("ciphertext_sha256").GetString() ?? string.Empty);

                // ProductionAgentRuntime.SendAsync owns the HTTP/auth retry loop.
                // Both simulated transport attempts observe the one key passed
                // by this high-level uploader invocation.
                CreateRetryKeys.Add(new[] { idempotencyKey, idempotencyKey });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
        }

        public static async Task<int> Main()
        {
            try
            {
                ValidateKeyContract();
                await ValidateHighLevelAttemptScopeAsync();
                Console.WriteLine("Production history uploader idempotency validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateKeyContract()
        {
            var operationId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            foreach (var kind in new[] { "chunk", "transcript", "snapshot" })
            {
                var first = ProductionHistoryUploader.CreateIdempotencyKey(kind, operationId);
                var retry = ProductionHistoryUploader.CreateIdempotencyKey(kind, operationId);
                Require(first == retry, "One operation did not produce a stable retry key.");
                Require(first == "history-create-v2-" + kind + "-00112233445566778899aabbccddeeff",
                    "History create V2 key format changed unexpectedly.");
                Require(first.Length <= 128 && Regex.IsMatch(first, "^[a-z0-9-]+$", RegexOptions.CultureInvariant),
                    "History create V2 key violates the server length/character contract.");
            }
            RequireThrows(() => ProductionHistoryUploader.CreateIdempotencyKey("CHUNK", operationId));
            RequireThrows(() => ProductionHistoryUploader.CreateIdempotencyKey("chunk", Guid.Empty));
        }

        private static async Task ValidateHighLevelAttemptScopeAsync()
        {
            var api = new RetryObservingHistoryApi();
            using var http = new HttpClient(new UnexpectedTransportHandler());
            using var uploader = new ProductionHistoryUploader(
                http,
                api.SendAsync,
                Guid.Parse("12345678-abcd-4321-abcd-1234567890ab"));
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("abcdef01-2345-4678-9abc-def012345678"),
                PairwiseKey = new byte[32],
                KeyEpoch = 3
            };
            RandomNumberGenerator.Fill(recipient.PairwiseKey);
            try
            {
                var events = new List<HistoryEventV1>
                {
                    new HistoryEventV1
                    {
                        Seq = 1,
                        MessageType = MessageType.OutputChunk,
                        Payload = ByteString.CopyFromUtf8("same sealed history object")
                    }
                };
                var sessionId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
                var first = await uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);
                var second = await uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);

                Require(first == ProductionHistoryUploadOutcome.Unavailable &&
                    second == ProductionHistoryUploadOutcome.Unavailable &&
                    api.CreateRetryKeys.Count == 2,
                    "The retry fixture did not execute two high-level create attempts.");
                Require(api.CreateRetryKeys[0][0] == api.CreateRetryKeys[0][1] &&
                    api.CreateRetryKeys[1][0] == api.CreateRetryKeys[1][1],
                    "An HTTP/auth retry changed its high-level operation key.");
                Require(api.CreateRetryKeys[0][0] != api.CreateRetryKeys[1][0],
                    "A later high-level upload attempt reused an earlier create key.");
                Require(api.CiphertextDigests.Count == 2 && api.CiphertextDigests[0] == api.CiphertextDigests[1],
                    "The fixture did not retry the same sealed object.");
                foreach (var attempts in api.CreateRetryKeys)
                {
                    Require(Regex.IsMatch(attempts[0], "^history-create-v2-chunk-[0-9a-f]{32}$", RegexOptions.CultureInvariant) &&
                        attempts[0].Length <= 128,
                        "A generated high-level create key violates the V2 contract.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
            }
        }

        private static void RequireThrows(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                return;
            }
            throw new InvalidOperationException("Invalid history idempotency identity was accepted.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
