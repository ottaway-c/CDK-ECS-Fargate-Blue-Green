using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using System.Collections.Generic;
using HealthCheck = Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck;
using Protocol = Amazon.CDK.AWS.ElasticLoadBalancingV2.Protocol;

namespace CdkEcsFargateBlueGreen.Infra
{
    public class ApiServiceStackProps : StackProps
    {
        public string SecurityGroupId { get; set; }
        public string VpcId { get; set; }
        public string LoadBalancerArn { get; set; }
        public string ClusterName { get; set; }
        public string ClusterArn { get; set; }
        public bool EnableCodeDeployBlueGreenHook { get; set; }
    }
    
    public class ApiServiceStack : Stack
    {
        public ApiServiceStack(Construct scope, string id, ApiServiceStackProps props) : base(scope, id, props)
        {
            // Note:
            // The CodeDeploy template hook prevents changes to the ECS resources and changes to non-ECS resources
            // from occurring in the same stack update, because the stack update cannot be done in a safe blue-green fashion.
            // Always use the "cdk --no-version-reporting" flag with this example.
            // This is to ensure the "AWS::CDK::Metadata" resource is not included in the included template.
            // If the "AWS::CDK::Metadata" changes, the CodeDeploy hook will return an error about non-ECS resource changes.

            // 
            // VPC, cluster, load balancers etc
            //

            // Note:
            // The CodeDeploy hook is not compatible with stack imports, so these dependencies must be looked up at stack synth time.
            var vpc = Vpc.FromLookup(this, "vpc", new VpcLookupOptions
            {
                VpcId = props.VpcId
            });

            var securityGroup = SecurityGroup.FromLookup(this, "security-group", props.SecurityGroupId);

            var loadBalancer = ApplicationLoadBalancer.FromLookup(this, "load-balancer", new ApplicationLoadBalancerLookupOptions
            {
                LoadBalancerArn = props.LoadBalancerArn
            });

            var cluster = Cluster.FromClusterAttributes(this, "ecs-cluster", new ClusterAttributes
            {
                ClusterArn = props.ClusterArn,
                ClusterName = props.ClusterName,
                SecurityGroups = new ISecurityGroup[]
                {
                    securityGroup
                },
                Vpc = vpc
            });

            //
            // Target groups
            //

            // Note:
            // We need two target groups that the ECS containers can be registered to.
            // CodeDeploy will shift traffic between these two target groups.
            // For simplicity I have made the listeners HTTP only, to avoid provisioning ACM certs
            var blueTargetGroup = new ApplicationTargetGroup(this, "api-target-group-blue", new ApplicationTargetGroupProps
            {
                TargetGroupName = "api-blue-target-group",
                Port = 80,
                Protocol = ApplicationProtocol.HTTP,
                TargetType = TargetType.IP,
                Vpc = vpc,
                DeregistrationDelay = Duration.Seconds(5),
                HealthCheck = new HealthCheck
                {
                    Interval = Duration.Seconds(5),
                    Path = "/",
                    Protocol = Protocol.HTTP,
                    HealthyHttpCodes = "200",
                    HealthyThresholdCount = 2,
                    UnhealthyThresholdCount = 3,
                    Timeout = Duration.Seconds(4)
                },
            });

            var greenTargetGroup = new ApplicationTargetGroup(this, "api-target-group-green", new ApplicationTargetGroupProps
            {
                TargetGroupName = "api-green-target-group",
                Port = 80,
                Protocol = ApplicationProtocol.HTTP,
                TargetType = TargetType.IP,
                Vpc = vpc,
                DeregistrationDelay = Duration.Seconds(5),
                HealthCheck = new HealthCheck
                {
                    Interval = Duration.Seconds(5),
                    Path = "/",
                    Protocol = Protocol.HTTP,
                    HealthyHttpCodes = "200",
                    HealthyThresholdCount = 2,
                    UnhealthyThresholdCount = 3,
                    Timeout = Duration.Seconds(4)
                }
            });

            //
            // Listener rules 
            //

            // Note:
            // CodeDeploy will shift traffic from blue to green and vice-versa
            // in both the production and test listeners.
            // The production listener is used for normal, production traffic.
            // The test listener is used for test traffic, like integration tests
            // which can run as part of a CodeDeploy lifecycle event hook prior to
            // traffic being shifted in the production listener.
            // Both listeners initially point towards the blue target group.
            var productionListener = loadBalancer.AddListener("api-production-listener", new ApplicationListenerProps
            {
                Port = 80,
                Protocol = ApplicationProtocol.HTTP,
                Open = true,
                DefaultAction = ListenerAction.WeightedForward(new IWeightedTargetGroup[]
                {
                    new WeightedTargetGroup
                    {
                        TargetGroup = blueTargetGroup,
                        Weight = 100
                    }
                })
            });

            var testListener = loadBalancer.AddListener("api-test-listener", new ApplicationListenerProps
            {
                Port = 9002, // Test traffic port
                Protocol = ApplicationProtocol.HTTP,
                Open = true,
                DefaultAction = ListenerAction.WeightedForward(new IWeightedTargetGroup[]
                {
                    new WeightedTargetGroup
                    {
                        TargetGroup = blueTargetGroup,
                        Weight = 100
                    }
                })
            });

            //
            // ECS Resources: task definition, service, task set, etc -
            //

            // Note:
            // The CodeDeploy blue-green hook will take care of orchestrating the sequence of steps
            // that CloudFormation takes during the deployment: the creation of the 'green' task set,
            // shifting traffic to the new task set, and draining/deleting the 'blue' task set.
            // The 'blue' task set is initially provisioned pointing to the 'blue' target group.

            var logGroup = new LogGroup(this, "api-logs", new LogGroupProps
            {
                LogGroupName = "api-logs",
                RemovalPolicy = RemovalPolicy.DESTROY,
                Retention = RetentionDays.ONE_DAY
            });

            // Note:
            // Updates to the task definition (or the container definition) will trigger the blue/green code deploy hook.
            var taskDefinition = new FargateTaskDefinition(this, "api-task-definition", new FargateTaskDefinitionProps
            {
                Cpu = 256,
            });
            taskDefinition.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

            var container = taskDefinition.AddContainer("api", new ContainerDefinitionOptions
            {
                Image = ContainerImage.FromRegistry("amazon/amazon-ecs-sample"),
                Logging = new AwsLogDriver(new AwsLogDriverProps { StreamPrefix = "api", LogGroup = logGroup }),
                Environment = new Dictionary<string, string>
                {
                    {"ENVIRONMENT", "Test" },
                    {"BLUE_GREEN", "Plz" }
                }
            });
            container.AddPortMappings(new PortMapping { ContainerPort = 80 });

            var service = new CfnService(this, "api-service", new CfnServiceProps
            {
                Cluster = cluster.ClusterName,
                DesiredCount = 1,
                DeploymentController = new CfnService.DeploymentControllerProperty
                {
                    Type = DeploymentControllerType.EXTERNAL.ToString()
                },
                PropagateTags = PropagatedTagSource.SERVICE.ToString(),
            });
            service.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

            service.Node.AddDependency(blueTargetGroup);
            service.Node.AddDependency(greenTargetGroup);
            service.Node.AddDependency(productionListener);
            service.Node.AddDependency(testListener);

            var taskSet = new CfnTaskSet(this, "api-task-set", new CfnTaskSetProps
            {
                Cluster = cluster.ClusterName,
                Service = service.AttrName,
                Scale = new CfnTaskSet.ScaleProperty
                {
                    Unit = Unit.PERCENT.ToString(),
                    Value = 100
                },
                TaskDefinition = taskDefinition.TaskDefinitionArn,
                LaunchType = LaunchType.FARGATE.ToString(),
                LoadBalancers = new[]
                {
                    new CfnTaskSet.LoadBalancerProperty
                    {
                        ContainerName = container.ContainerName,
                        ContainerPort = container.ContainerPort,
                        TargetGroupArn = blueTargetGroup.TargetGroupArn
                    }
                },
                NetworkConfiguration = new CfnTaskSet.NetworkConfigurationProperty
                {
                    AwsVpcConfiguration = new CfnTaskSet.AwsVpcConfigurationProperty
                    {
                        AssignPublicIp = "DISABLED",
                        SecurityGroups = new[]
                        {
                            securityGroup.SecurityGroupId
                        },
                        Subnets = vpc.SelectSubnets(new SubnetSelection { SubnetType = SubnetType.PRIVATE }).SubnetIds
                    }
                },
            });

            var primaryTaskSet = new CfnPrimaryTaskSet(this, "api-primary-task-set", new CfnPrimaryTaskSetProps
            {
                Cluster = cluster.ClusterName,
                Service = service.AttrName,
                TaskSetId = taskSet.AttrId
            });

            //
            // CodeDeploy hook, IAM role and cloud formation transform to configure the blue-green deployments
            //
            
            var codeDeployServiceRole = new Role(this, "api-code-deploy-service-role", new RoleProps
            {
                AssumedBy = new ServicePrincipal("codedeploy.amazonaws.com"),
                RoleName = "api-ecs-code-deploy-service-role"
            });

            codeDeployServiceRole.AttachInlinePolicy(new Policy(this, "api-code-deploy-blue-green-policy", new PolicyProps
            {
                PolicyName = "code-deploy-blue-green-policy",
                Document = new PolicyDocument(new PolicyDocumentProps
                {
                    Statements = new[]
                    {
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Actions = new []
                            {
                                "codedeploy:Get*",
                                "codedeploy:CreateCloudFormationDeployment"
                            },
                            Effect = Effect.ALLOW,
                            Resources = new []
                            {
                                "arn:aws:codedeploy:*"
                            }
                        })
                    }
                })
            }));

            // Note:
            // If performing non-ECS resource changes, e.g those resources not directly referenced by the blue-green hook:
            // 1. Temporarily disable the hook
            // 2. Deploy the non-ECS resource changes
            // 3. Reenable the hook and redeploy the stack
            if (props.EnableCodeDeployBlueGreenHook)
            {
                this.AddTransform("AWS::CodeDeployBlueGreen");

                var taskDefinitionLogicalId = this.GetLogicalId(taskDefinition.Node.DefaultChild as CfnTaskDefinition);
                var taskSetLogicalId = this.GetLogicalId(taskSet);

                var blueGreenHook = new CfnCodeDeployBlueGreenHook(this, "api-service-code-deploy-blue-green-hook", new CfnCodeDeployBlueGreenHookProps
                {
                    TrafficRoutingConfig = new CfnTrafficRoutingConfig
                    {
                        TimeBasedCanary = new CfnTrafficRoutingTimeBasedCanary
                        {
                            // Shift 20% of prod traffic, then wait 1 minute
                            StepPercentage = 20,
                            BakeTimeMins = 1,
                        },
                        Type = CfnTrafficRoutingType.TIME_BASED_CANARY
                    },
                    AdditionalOptions = new CfnCodeDeployBlueGreenAdditionalOptions
                    {
                        // After canary period, shift 100% of prod traffic, then wait 1 minute before terminating the old tasks
                        TerminationWaitTimeInMinutes = 1
                    },
                    ServiceRole = this.GetLogicalId(codeDeployServiceRole.Node.DefaultChild as CfnRole),
                    Applications = new ICfnCodeDeployBlueGreenApplication[]
                    {
                        new CfnCodeDeployBlueGreenApplication
                        {
                            Target = new CfnCodeDeployBlueGreenApplicationTarget
                            {
                                Type = service.CfnResourceType,
                                LogicalId = this.GetLogicalId(service)
                            },
                            EcsAttributes = new CfnCodeDeployBlueGreenEcsAttributes
                            {
                                TaskDefinitions = new[]
                                {
                                    taskDefinitionLogicalId,
                                    taskDefinitionLogicalId + "green"
                                },
                                TaskSets = new[]
                                {
                                    taskSetLogicalId,
                                    taskSetLogicalId + "green"
                                },
                                TrafficRouting = new CfnTrafficRouting
                                {
                                    ProdTrafficRoute = new CfnTrafficRoute
                                    {
                                        Type = CfnListener.CFN_RESOURCE_TYPE_NAME,
                                        LogicalId = this.GetLogicalId(
                                            productionListener.Node.DefaultChild as CfnListener)
                                    },
                                    TestTrafficRoute = new CfnTrafficRoute
                                    {
                                        Type = CfnListener.CFN_RESOURCE_TYPE_NAME,
                                        LogicalId = this.GetLogicalId(testListener.Node.DefaultChild as CfnListener)
                                    },
                                    TargetGroups = new[]
                                    {
                                        this.GetLogicalId(blueTargetGroup.Node.DefaultChild as CfnTargetGroup),
                                        this.GetLogicalId(greenTargetGroup.Node.DefaultChild as CfnTargetGroup)
                                    }
                                }
                            }
                        },
                    }
                });
            }

            // Note:
            // Although not well documented, the blue-green hook requires this parameter to be present in the template.
            // Without the parameter, the blue-green update will fail with "Template parameters modified by transform"
            // See https://stackoverflow.com/a/64701206
            new CfnParameter(this, "Vpc", new CfnParameterProps
            {
                Type = "AWS::EC2::VPC::Id",
                Default = vpc.VpcId
            });
        }
    }
}
