using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{
    /**
     * This is a one-directional fat link with some delay (specified in
     * cycles). Will be pumped by the simulator once per cycle.
     *
     * Also includes a sideband (that should flow in the opposite
     * direction) that carries arbitrary objects. Used, for example,
     * for intelligent deflections to notify neighbors of congestion.
     * 
     * The purpose of this class is to provide the network with links
     * with multiple width
     */
    public class FatLink : Link_Interface
    {
        /**
         * Constructs a Link. Note that delay specifies _additional_ cycles; that is, if
         * delay == 0, then this.In will appear at this.Out after _one_ doStep() iteration.
         */
        public FatLink(int delay, int node, int link) : base(delay, node, link)
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
        }
    }
}
