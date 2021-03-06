using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using KeyVault.CertificateRotation.Internal;

using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Management.FrontDoor;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace KeyVault.CertificateRotation
{
    public class FrontDoorRotationFunction
    {
        public FrontDoorRotationFunction(KeyVaultClient keyVaultClient, FrontDoorManagementClient frontDoorManagementClient)
        {
            _keyVaultClient = keyVaultClient;
            _frontDoorManagementClient = frontDoorManagementClient;
        }

        private readonly KeyVaultClient _keyVaultClient;
        private readonly FrontDoorManagementClient _frontDoorManagementClient;

        [FunctionName(nameof(FrontDoorRotation))]
        public async Task FrontDoorRotation([TimerTrigger("0 0 0 * * *")] TimerInfo timer, ILogger log)
        {
            var tasks = new List<Task>();

            var frontDoors = await _frontDoorManagementClient.FrontDoors.SafeListAllAsync();

            foreach (var frontDoor in frontDoors)
            {
                log.LogInformation($"Front Door: {frontDoor.Name}");

                var resourceGroupName = frontDoor.ResourceGroupName();

                var frontendEndpoints = await _frontDoorManagementClient.FrontendEndpoints.ListAllByFrontDoorAsync(resourceGroupName, frontDoor.Name);

                foreach (var frontendEndpoint in frontendEndpoints)
                {
                    log.LogInformation($"Frontend Endpoint: {frontendEndpoint.Name}");

                    if (frontendEndpoint.CustomHttpsConfiguration?.CertificateSource != "AzureKeyVault")
                    {
                        continue;
                    }

                    var vaultName = ExtractVaultName(frontendEndpoint.CustomHttpsConfiguration.Vault.Id);

                    log.LogInformation($"Vault Name: {vaultName}");
                    log.LogInformation($"Secret Name: {frontendEndpoint.CustomHttpsConfiguration.SecretName}");
                    log.LogInformation($"Secret Version: {frontendEndpoint.CustomHttpsConfiguration.SecretVersion}");

                    var latestCertificate = await _keyVaultClient.GetCertificateAsync(
                        $"https://{vaultName}.vault.azure.net/",
                        frontendEndpoint.CustomHttpsConfiguration.SecretName);

                    if (latestCertificate.CertificateIdentifier.Version == frontendEndpoint.CustomHttpsConfiguration.SecretVersion)
                    {
                        continue;
                    }

                    log.LogInformation($"Target Secret Version: {latestCertificate.CertificateIdentifier.Version}");

                    frontendEndpoint.CustomHttpsConfiguration.SecretVersion = latestCertificate.CertificateIdentifier.Version;

                    tasks.Add(_frontDoorManagementClient.FrontendEndpoints.EnableHttpsAsync(resourceGroupName, frontDoor.Name, frontendEndpoint.Name, frontendEndpoint.CustomHttpsConfiguration));
                }
            }

            await Task.WhenAll(tasks);
        }

        private static string ExtractVaultName(string resourceId)
        {
            var values = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

            return values[7];
        }
    }
}
