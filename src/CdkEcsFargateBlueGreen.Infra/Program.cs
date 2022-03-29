using Amazon.CDK;
using dotenv.net;
using System.Collections.Generic;
using System.IO;
using Environment = Amazon.CDK.Environment;

namespace CdkEcsFargateBlueGreen.Infra
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            DotEnv.Load(new DotEnvOptions(envFilePaths: new List<string>{path}));

            var awsAccountId = System.Environment.GetEnvironmentVariable("AWS_ACCOUNT_ID");
            var region = System.Environment.GetEnvironmentVariable("AWS_REGION");

            var app = new App();

            var networkStack = new NetworkStack(app, "network", new NetworkStackProps
            {
                Env = new Environment
                {
                    Account = awsAccountId,
                    Region = region
                }
            });
            
            var clusterStack = new ClusterStack(app, "cluster", new ClusterStackProps
            {
                Vpc = networkStack.Vpc,
                StackName = "cluster-test",
                Env = new Environment
                {
                    Account = awsAccountId,
                    Region = region
                }
            });

            // Note:
            // The network and cluster stacks need to be deployed before deploying the api stack.
            // The Blue/Green hook is not compatible with stack imports/outputs,
            // so the inputs must be passed in manually and resolved at stack synth.
            bool.TryParse(System.Environment.GetEnvironmentVariable("SYNTH_API_STACK"), out var synthApiStack);
            
            if (synthApiStack)
            {
                var vpcId = System.Environment.GetEnvironmentVariable("VPC_ID");
                var securityGroupId = System.Environment.GetEnvironmentVariable("SECURITY_GROUP_ID");
                var loadBalancerArn = System.Environment.GetEnvironmentVariable("LOAD_BALANCER_ARN");
                var clusterArn = System.Environment.GetEnvironmentVariable("CLUSTER_ARN");
                var clusterName = System.Environment.GetEnvironmentVariable("CLUSTER_NAME");

                var apiServiceStack = new ApiServiceStack(app, "api-service", new ApiServiceStackProps
                {
                    StackName = "api-service-test",
                    Env = new Environment
                    {
                        Account = awsAccountId,
                        Region = region
                    },
                    DeploymentType = DeploymentType.BlueGreen,
                    VpcId = vpcId,
                    SecurityGroupId = securityGroupId,
                    LoadBalancerArn = loadBalancerArn,
                    ClusterArn = clusterArn,
                    ClusterName = clusterName,
                });
            }

            Tags.Of(app).Add("github-url", "https://github.com/ottaway-c/CDK-ECS-Fargate-Blue-Green");
            
            app.Synth();
        }
    }
}
