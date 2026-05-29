using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QSoft.ETW
{
    public partial class ETW
    {
    }

    public class TracerBuilder
    {
        public TracerBuilder TraceKernel()
        {
            return this;
        }

        public Tracer Build(string filename)
        {
            return new Tracer();
        }
    }

    public class Tracer
    {

    }

}
