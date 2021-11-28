using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;

namespace CdkEcsFargateBlueGreen.Infra
{
    public class ClusterStackProps : StackProps
    {
        public Vpc Vpc { get; set; }
    }

    public class ClusterStack : Stack
    {
        public ClusterStack(Construct scope, string id, ClusterStackProps props) : base(scope, id, props)
        {
            var cluster = new Cluster(this, "ecs-cluster", new ClusterProps
            {
                ClusterName = "ecs-cluster",
                ContainerInsights = true,
                Vpc = props.Vpc
            });

            new CfnOutput(this, "ecs-cluster-arn", new CfnOutputProps
            {
                ExportName = "ecs-cluster-arn",
                Value = cluster.ClusterArn
            });

            new CfnOutput(this, "ecs-cluster-name", new CfnOutputProps
            {
                ExportName = "ecs-cluster-name",
                Value = cluster.ClusterName
            });

            var securityGroup = new SecurityGroup(this, "ecs-cluster-security-group", new SecurityGroupProps
            {
                Vpc = props.Vpc
            });
            securityGroup.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

            new CfnOutput(this, "ecs-cluster-security-group-id", new CfnOutputProps
            {
                ExportName = "ecs-cluster-security-group-id",
                Value = securityGroup.SecurityGroupId
            });

            var loadBalancer = new ApplicationLoadBalancer(this, "ecs-cluster-load-balancer", new ApplicationLoadBalancerProps
            {
                LoadBalancerName = "ecs-cluster-load-balancer",
                Vpc = props.Vpc,
                InternetFacing = false,
                SecurityGroup = securityGroup
            });
            loadBalancer.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

            new CfnOutput(this, "ecs-cluster-load-balancer-dns-name", new CfnOutputProps
            {
                ExportName = "ecs-cluster-load-balancer-dns-name",
                Value = loadBalancer.LoadBalancerDnsName
            });

            securityGroup.Connections.AllowFrom(loadBalancer, Port.Tcp(80));
        }
    }
}
