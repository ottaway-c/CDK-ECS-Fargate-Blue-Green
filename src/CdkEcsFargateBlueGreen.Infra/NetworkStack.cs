using Amazon.CDK;
using Amazon.CDK.AWS.EC2;

namespace CdkEcsFargateBlueGreen.Infra
{
    public class NetworkStackProps : StackProps
    {
    }

    public class NetworkStack : Stack
    {
        public Vpc Vpc { get; }
        
        public NetworkStack(Construct scope, string id, NetworkStackProps props) : base(scope, id, props)
        {
            Vpc = new Vpc(this, "vpc");
            Vpc.ApplyRemovalPolicy(RemovalPolicy.DESTROY);

            new CfnOutput(this, "vpc-id", new CfnOutputProps
            {
                ExportName = "vpc-id",
                Value = Vpc.VpcId
            });
        }
    }
}
