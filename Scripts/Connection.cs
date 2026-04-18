namespace XNode
{
    public class Connection
    {
        public NodePort Input;
        public NodePort Output;

        public Connection(NodePort input, NodePort output)
        {
            Input = input;
            Output = output;
        }

        public Connection()
        {

        }
    }
}