using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{

    public abstract class Link_Interface
    {

        public int node_ID;
        public int link_ID;

        public int m_delay;
        public Flit[] m_fifo;
        public object[] m_sideband_fifo;

        public Flit In, Out;
        public object SideBandIn, SideBandOut;

        public int numLink;

        public Link_Interface(int delay, int node, int link)
        {
            m_delay = delay;
            if (m_delay > 0)
                m_fifo = new Flit[m_delay];
            else
                m_fifo = null;

            SideBandOut = SideBandIn = null;
            if (m_delay > 0)
                m_sideband_fifo = new object[m_delay];
            else
                m_sideband_fifo = null;
            node_ID = node;
            link_ID = link;
            if(Config.bFtfly) //FbFly
                numLink = 8;
            else
                numLink = 4; //Mesh
        }

        public abstract void doStep();

        public void doStat()
        {
            Simulator.stats.link_used[(this.node_ID*this.numLink)+this.link_ID].Add();
        }

        public void flush()
        {
            for (int i = 0; i < m_delay; i++)
            {
                m_fifo[i] = null;
                m_sideband_fifo[i] = null;
            }
            Out = null;
            In = null;
            SideBandOut = null;
            SideBandIn = null;
        }

        public void visitFlits(Flit.Visitor fv)
        {
            for (int i = 0; i < m_delay; i++)
                if (m_fifo[i] != null)
                    fv(m_fifo[i]);

            if (Out != null)
                fv(Out);
            if (In != null)
                fv(In);
        }

    }

    /**
     * This is a one-directional link with some delay (specified in
     * cycles). Will be pumped by the simulator once per cycle.
     *
     * Also includes a sideband (that should flow in the opposite
     * direction) that carries arbitrary objects. Used, for example,
     * for intelligent deflections to notify neighbors of congestion.
     */
    public class Link : Link_Interface
    {

        /**
         * Constructs a Link. Note that delay specifies _additional_ cycles; that is, if
         * delay == 0, then this.In will appear at this.Out after _one_ doStep() iteration.
         */
        public Link(int delay, int node, int link) : base(delay, node, link)
        {
        }

        public override void doStep()
        {
            if (m_delay > 0)
            {
                Out = m_fifo[0];
                for (int i = 0; i < m_delay - 1; i++)
                    m_fifo[i] = m_fifo[i + 1];
                m_fifo[m_delay - 1] = In;

                SideBandOut = m_sideband_fifo[0];
                for (int i = 0; i < m_delay - 1; i++)
                    m_sideband_fifo[i] = m_sideband_fifo[i + 1];
                m_sideband_fifo[m_delay - 1] = SideBandIn;
            }
            else
            {
                Out = In;
                SideBandOut = SideBandIn;
            }


            In = null;
            SideBandIn = null;

//            doStat();
        }
    }
}
