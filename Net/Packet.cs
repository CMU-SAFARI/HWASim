//#define DEBUG

using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;

namespace ICSimulator
{
    public struct Coord
    {
        public int x;
        public int y;
        public int ID;    // identifier of the node. 

        public Coord(int x, int y)
        {
            this.x = x;
            this.y = y;
            ID = getIDfromXY(x, y);
        }

        public Coord(int ID)
        {
            this.ID = ID;
            getXYfromID(ID, out x, out y);
        }

        public override bool Equals(object obj)
        {
            return (obj is Coord) && ((Coord)obj).x == x && ((Coord)obj).y == y;
        }

        public override int GetHashCode()
        {
            return ID.GetHashCode(); // x.GetHashCode() ^ y.GetHashCode();
        }

        public override string ToString()
        {
            return "(" + x + "," + y + ")";
        }

        public static int getIDfromXY(int x, int y)
        {
            return x * Config.network_nrY + y;
        }

        public static void getXYfromID(int id, out int x, out int y)
        {
            x = id / Config.network_nrY;
            y = id % Config.network_nrY;
        }
    }

    public class Packet
    {
        public delegate void Sender(Packet p);

        private static ulong nrPackets = 0;

        public Coord src { get { return _src; } }
        private Coord _src;
        public Coord dest { get { return _dest; } }
        private Coord _dest;

        public ulong ID { get { return _ID; } }
        private ulong _ID;

        public Request request { get { return _request; } }
        private Request _request;

        public int requesterID;

        public ulong seq;
 
        public bool from_GPU;

        public bool flow_open; // grab slot; queue, bounce for retx if none avail
        public bool flow_close; // release slot
        public int retx_count;

        // N.B.:
        // block number may be different than that in the request: as with
        // CachePacket above, a request is associated with a packet because
        // that packet is due to the request, but the packet may not be
        // delivering data for that request (e.g., it may be a writeback)
        public ulong block { get { return _block; } }
        private ulong _block;

        public ulong creationTime;
        public ulong injectionTime;

        public Flit[] flits;
        public int nrOfFlits; // { get { return flits.Length; } }

        public int nrOfArrivedFlits = 0;

        //TODO: move these into router-policy aware structures
        #region TO_BE_ISOLATED
        public int MIN_AD_dir;

        public ulong staticPriority; //summary> Static portion of prioritization. </summary>
        public ulong batchID;
        #endregion

        // SCARAB
        public int scarab_retransmit_count;
        public Packet scarab_retransmit;
        public bool scarab_is_nack, scarab_is_teardown;

        public Packet(Request request, ulong block, int nrOfFlits, Coord source, Coord dest)
        {
            _request = request;
            if (_request != null)
                _request.beenToNetwork = true;
            _block = block; // may not come from request (see above)
            _src = source;
            _dest = dest;
            if (request != null)
                request.setCarrier(this);
            requesterID = -1;
            initialize(Simulator.CurrentRound, nrOfFlits);
        }

        /**
         * Always call this initialization method before using a packet. All flits are also appropriately initialized
         */
        public void initialize(ulong creationTime, int nrOfFlits)
        {
            _ID = Packet.nrPackets;
            Packet.nrPackets++;

            batchID = (Simulator.CurrentRound / Config.STC_batchPeriod) % Config.STC_batchCount;

            this.nrOfFlits = nrOfFlits;

            flits = new Flit[nrOfFlits];
            for (int i = 0; i < nrOfFlits; i++)
                flits[i] = new Flit(this, i);

            flits[0].isHeadFlit = true;
            for (int i = 1; i < nrOfFlits; i++)
                flits[i].isHeadFlit = false;

            this.creationTime = creationTime;
            injectionTime = ulong.MaxValue;
            nrOfArrivedFlits = 0;

            for (int i = 0; i < nrOfFlits; i++)
            {
                flits[i].hasFlitArrived = false;
                flits[i].nrOfDeflections = 0;
            }


            for (int i = 0; i < nrOfFlits; i++)
            {
                flits[i].isTailFlit = false;
                flits[i].isHeadFlit = false;
            }

            flits[0].isHeadFlit = true;
            flits[nrOfFlits - 1].isTailFlit = true;

            flow_open = false;
            flow_close = false;
            retx_count = 0;

            scarab_retransmit_count = 0;
            scarab_retransmit = null;
            scarab_is_nack = false;
            scarab_is_teardown = false;
        }

        public void setRequest(Request req)
        {
            _request = req;
            req.setCarrier(this);
        }

        public static int numQueues
        {
            get
            {
                if (Config.split_queues)
                    return 3; // control, data response, WB (off critical path)
                else
                    return 1;
            }
        }

        public virtual int getQueue()
        {
            return 0; // should be overridden
        }

        public virtual int getClass()
        {
            return 0; // should be overridden
        }
    }

    public class Flit
    {
        public Packet packet;
        public int flitNr;
        public bool hasFlitArrived;
        public bool isHeadFlit;
        public bool isTailFlit;
        public ulong nrOfDeflections;
        public int virtualChannel; // to which virtual channel the packet should go in the next router. 
        public bool sortnet_winner;

        public int currentX;
        public int currentY;

        public bool Deflected;

        public ulong injectionTime; // absolute injection timestamp
        public ulong headT; // reaches-head-of-queue timestamp

        public int nackWire; // nack wire nr. for last hop
        public int inDir;
        public int prefDir;

        public enum State { Normal, Placeholder, Rescuer, Carrier }
        public State state;
        public Coord rescuerCoord;
        
        public bool isSilver; // Is the flit silver for the current round
        public bool wasDeflected; // Deflection Deprioritization


        //TODO: use this to find out which flit keeps on getting swapped out
        // throw exception when count > 100
        public ulong swapOutCount = 0;

        public Coord dest
        {
            get
            {
                switch (state)
                {
                    case State.Normal: return packet.dest;
                    case State.Carrier: return packet.dest;

                    case State.Rescuer: return rescuerCoord;

                    case State.Placeholder: return new Coord(0);
                }
                throw new Exception("Unknown flit state");
            }
        }

        public ulong distance;
        //private bool[] deflections;
        //private int deflectionsIndex;
        public Flit(Packet packet, int flitNr)
        {
            this.packet = packet;
            this.flitNr = flitNr;
            hasFlitArrived = false;
            this.Deflected = false;
            //deflections = new bool[100];
            //deflectionsIndex = 0;
            if (packet != null)
                distance = Simulator.distance(packet.src, packet.dest);
        }
        /*
        public void deflectTest()
        {
            if (deflectionsIndex == 100)
                return;
            //Console.WriteLine("{0} {1}", Deflected, deflectionsIndex);
            deflections[deflectionsIndex] = this.Deflected;
            //Console.WriteLine("{0} {1}", deflections[deflectionsIndex], deflectionsIndex);

            deflectionsIndex++;
        }
        public void dumpDeflections()
        {
            for (int i = 0; i < deflectionsIndex; i++)
                Console.Write(deflections[i] ? "D" : "-");
            Console.WriteLine();
        }*/

        public delegate void Visitor(Flit f);

        public override string ToString()
        {
            if (packet != null)
                return String.Format("Flit {0} of packet {1} (state {2})", flitNr, packet, state);
            else
                return String.Format("Flit {0} of packet <NONE> (state {1})", flitNr, state);
        }
    }
}
