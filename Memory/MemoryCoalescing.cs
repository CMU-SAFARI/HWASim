using System;
using System.Collections.Generic;
using System.IO;

namespace ICSimulator
{
    public class Tuple3
    {
        public int Item1;
        public ulong Item2;
        public MemoryRequest Item3;

        public Tuple3(int i1, ulong i2, MemoryRequest i3)
        {
            Item1 = i1;
            Item2 = i2;
            Item3 = i3;
        }
    }

    public class MemoryCoalescing
    {
        public Queue<Tuple3>[,] clientQueue;
        public int numClients;
        int currentClient = 0;
        int currentRead = 0;
        int consecutiveRequests = 0; // from same client
        ulong whenSwitchReadWrite = 0;
        int[,] numInFlightRequests;

        int A;
        int B;
        int streakMax;
        int rwSwitchThreshold;
        int urgentThreshold;
        int lazyThreshold;

        int numChannels;
        int numRanks;
        int numBanks;
        ulong[,,] currentRow;
        ulong[,,] whenAvailable; // approx DRAM timing - only GPU's view; doesn't see CPU traffic

        ulong MemoryCoalescingCombineMask;
        int MemoryCoalescingCombineShift;

        public MemoryCoalescing()
        {
            numClients = Enum.GetValues(typeof(TraceFile_AMD_GPU.ClientID)).Length;

            clientQueue = new Queue<Tuple3>[numClients,2];
            numInFlightRequests = new int[numClients,2];
            for(int i=0;i<numClients;i++)
                for(int j=0;j<2;j++)
                {
                    clientQueue[i,j] = new Queue<Tuple3>();
                    numInFlightRequests[i,j] = 0;
                }

            numChannels = Config.memory.numChannels;
            numRanks = Config.memory.numRanks;
            numBanks = Config.memory.numBanks;
            A = Config.memory.MemoryCoalescingAgeWeight;
            B = Config.memory.MemoryCoalescingCountWeight;
            streakMax = Config.memory.MemoryCoalescingStreakMax;
            rwSwitchThreshold = Config.memory.MemoryCoalescingRWSwitchThreshold;
            urgentThreshold = Config.memory.MemoryCoalescingUrgentThreshold;
            lazyThreshold = Config.memory.MemoryCoalescingLazyThreshold;

            MemoryCoalescingCombineMask = (ulong)(Config.memory.MemoryCoalescingCombineSize-1);
            MemoryCoalescingCombineShift = 5; // typical mreq.size = 32

            currentRow = new ulong[numChannels,numRanks,numBanks];
            whenAvailable = new ulong[numChannels,numRanks,numBanks];
            for(int c=0;c<numChannels;c++)
                for(int r=0;r<numRanks;r++)
                    for(int b=0;b<numBanks;b++)
                    {
                        currentRow[c,r,b] = ulong.MaxValue;
                        whenAvailable[c,r,b] = 0;
                    }
        }

        // Called by CPU.cs to issue a request to the MemoryCoalescing
        // This just places the request in the appropriate client queue
        // This cannot be used when we model the network
        public void issueReq(int targetID, Request req, Simulator.Ready cb)
        {
//            Console.WriteLine("In MemoryCoalescing, issueReq is called requester {0}, addr = {1} at cycle {2}", req.requesterID, req.address, Simulator.CurrentRound);
            MemoryRequest mreq = new MemoryRequest(req, cb);
            mreq.from_GPU = true;
            //Console.WriteLine("Get a GPU Request {0}", req.from_GPU);
            int c = (int)req.client;
            int w = req.write?1:0;
            if(Config.useMemoryCoalescing)
            {
//            Console.WriteLine("In MemoryCoalescing, enqueue to the client queue requester {0}, addr = {1} at cycle {2}", req.requesterID, req.address, Simulator.CurrentRound);
                clientQueue[c,w].Enqueue(new Tuple3(targetID,Simulator.CurrentRound,mreq));
            }
            else
            {
                bool l1hit = false, l1upgr = false, l1ev = false, l1wb = false;
                bool l2access = false, l2hit = false, l2ev = false, l2wb = false, c2c = false;
                Simulator.network.cache.access(req.requesterID, req.address, req.write, cb, out l1hit, out l1upgr, out l1ev, out l1wb, out l2access, out l2hit, out l2ev, out l2wb, out c2c);
            }
        }

        protected int ComputeWeight(int age, int count)
        {
            return A*age + B*count;
        }

        protected bool BankAvailable(MemoryRequest mreq)
        {
            return (mreq.shift_row == currentRow[mreq.channel_index,mreq.rank_index,mreq.bank_index]) ||
                    whenAvailable[mreq.channel_index,mreq.rank_index,mreq.bank_index] <= Simulator.CurrentRound;
        }

        protected bool Combinable(ulong p1, ulong p2)
        {
            ulong mask = ~MemoryCoalescingCombineMask;
            return (p1 & mask) == (p2 & mask);
        }

        protected uint GetCombineBit(MemoryRequest mreq)
        {
            int x = (int)(mreq.request.address & MemoryCoalescingCombineMask) >> MemoryCoalescingCombineShift;
            if(mreq.mem_size == 32)
                return (uint)1 << x;
            else if(mreq.mem_size == 64)
                return (uint)3 << x;
            else
                throw new Exception(String.Format("unsupported MemoryCoalescing mreq.mem_size: {0}",mreq.mem_size));
        }

        protected uint ConvertMaskToSize(uint mask)
        {
            uint count=0;
            uint m = mask;
            while(m > 0)
            {
                count += m&1;
                m >>= 1;
            }
            return 32 * count;
        }

        // This is needed just because of the wacky binding/scoping rules used by
        // delegates; without this, you can't keep recursively wrapping delegates
        // within a loop.
        protected void WrapDelegates(MemoryRequest m1, MemoryRequest m2)
        {
            Simulator.Ready prev_cb = m1.cb;
            m1.cb = delegate() { prev_cb(); m2.cb(); };
        }

        // The main scheduling function.  Walk the client queues and decide what to send
        // onward to the main (UNB) memory controller
        public void doStep()
        {
            int winnerClient = -1;
            int winnerRead = -1;
            int bestWeight = 0;
            bool seenUrgent = false;
            bool stillHitting = false;

            // 1. check to see if anyone has reached their urgency limit, if so, issue the oldest
            for(int c=0;c<numClients;c++)
            {
                for(int rw=0;rw<2;rw++)
                {
                    if(clientQueue[c,rw].Count > 0)
                    {
                        Tuple3 t = clientQueue[c,rw].Peek();
                        int dest = t.Item1;
                        MemoryRequest mreq = t.Item3;

                        if(BankAvailable(mreq) && Simulator.network.nodes[dest].mem.RequestEnqueueable(mreq))
                        {
                            int age = (int)(Simulator.CurrentRound - t.Item2);
                            if(age > urgentThreshold)
                            {
                                // if urgent, choose oldest
                                if(age > bestWeight)
                                {
                                    bestWeight = age;
                                    winnerClient = c;
                                    winnerRead = rw;
                                    seenUrgent = true;
                                }
                            }
                        }
                    }
                }
            }

            if(!seenUrgent) // nothing urgent
            {
                // TODO: update this to check that requests still hitting, although could potentially
                // be in a different bank (i.e., currentRow need not be same).  Need rough model of
                // what pages are open.  XXX: doesn't seem like MemoryCoalescing does this though

                // 2. else check to see if current client still has requests to same row
                //    if so and we haven't hit the streak limit, issue the request
                if(clientQueue[currentClient,currentRead].Count > 0)
                {
                    Tuple3 t = clientQueue[currentClient,currentRead].Peek();
                    int dest = t.Item1;
                    MemoryRequest mreq = t.Item3;
                    if(BankAvailable(mreq) &&
                       (mreq.shift_row == currentRow[mreq.channel_index,mreq.rank_index,mreq.bank_index]) &&
                       (consecutiveRequests < streakMax) &&
                       Simulator.network.nodes[dest].mem.RequestEnqueueable(mreq)) // still hitting same page
                    {
                        winnerClient = currentClient;
                        winnerRead = currentRead;
                        stillHitting = true;
                    }
                }

                if(consecutiveRequests >= streakMax)
                    consecutiveRequests = 0;

                // 3. if we haven't crossed the read/write switch threshold, scan the clients
                //    to see which queue to use next (else switch read/write and do same)
                if(!stillHitting)
                {
                    bool allLazy = true;

                    // This is messy: if we haven't passed the rwSwitchThreshold, then we
                    // only consider the current read/write mode (e.g., if we're reading,
                    // we don't consider any writes).  The least two bits of rwSet determine
                    // which we consider: in binary, ...000wr.  If below threshold, we AND
                    // out the other bit.
                    int rwSet = 3; // by default, bit mask set to consider both reads and writes
                    if((Simulator.CurrentRound - whenSwitchReadWrite) < (ulong)rwSwitchThreshold)
                        rwSet &= ~(1<<(1-currentRead));

                    for(int rw=0;rw<2;rw++)
                    {
                        if(((rwSet >> rw) & 1) != 0)
                            for(int c=0;c<numClients;c++)
                            {
                                if(clientQueue[c,rw].Count > 0)
                                {
                                    Tuple3 t = clientQueue[c,rw].Peek();
                                    int dest = t.Item1;
                                    int age = (int)(Simulator.CurrentRound - t.Item2);
                                    MemoryRequest mreq = t.Item3;
                                    int weight = ComputeWeight(age,clientQueue[c,rw].Count);
                                    bool lazy = age < lazyThreshold;

                                    if(BankAvailable(mreq) &&
                                       Simulator.network.nodes[dest].mem.RequestEnqueueable(mreq))
                                    {
                                        if(!lazy && allLazy)
                                        {
                                            winnerClient = c;
                                            winnerRead = rw;
                                            allLazy = false;
                                        }
                                        else if(lazy && !allLazy)
                                        {
                                            // if this is lazy but a non-lazy requests has been seen, skip
                                        }
                                        else if(weight > bestWeight) 
                                        {
                                            winnerClient = c;
                                            winnerRead = rw;
                                            bestWeight = weight;
                                        }
                                    }
                                }
                            }
                    }
                }
            }

//            Console.WriteLine("In MemoryCoalescing Dostep, winner client is {0} at cyc {1}",winnerClient,  Simulator.CurrentRound);
            if(winnerClient != -1)
            {
                Tuple3 t = clientQueue[winnerClient,winnerRead].Peek();
                int dest = t.Item1;
                MemoryRequest mreq = t.Item3;

                Simulator.Ready prev_cb = mreq.cb;
                mreq.cb = delegate() {
                    numInFlightRequests[winnerClient,winnerRead]--;
                    prev_cb();
                };
                
                clientQueue[winnerClient,winnerRead].Dequeue();
                numInFlightRequests[winnerClient,winnerRead]++;

                uint combineMask = GetCombineBit(mreq);

                // if next request is combinable, grab that, too.
                int numCombos = 1;
                int comboMax = Config.memory.MemoryCoalescingComboMax;
                if(clientQueue[winnerClient,winnerRead].Count > 0)
                {
                    Tuple3 n = clientQueue[winnerClient,winnerRead].Peek();
                    while(Combinable(mreq.request.address,n.Item3.request.address))
                    {
                        WrapDelegates(mreq,n.Item3);

                        combineMask |= GetCombineBit(mreq);
                        Simulator.stats.MemoryCoalescingNumCombinedRequests.Add();

                        clientQueue[winnerClient,winnerRead].Dequeue();
                         numCombos++;
                         if(numCombos >= comboMax) // limit number of requests that can be combined
                             break;

                        if(clientQueue[winnerClient,winnerRead].Count > 0)
                            n = clientQueue[winnerClient,winnerRead].Peek();
                        else
                            break;
                    }
                }

//                Console.WriteLine("In MemoryCoalescing, issueing request, access to NoCs (through CmpCache module requester {0}, addr = {1} at cycle {2}", mreq.request.requesterID, mreq.request.address, Simulator.CurrentRound);
                // edited for NoCs CmpCache
                bool l1hit = false, l1upgr = false, l1ev = false, l1wb = false;
                bool l2access = false, l2hit = false, l2ev = false, l2wb = false, c2c = false;
                Simulator.network.cache.access(mreq.request.requesterID, mreq.request.address, mreq.request.write, mreq.cb, out l1hit, out l1upgr, out l1ev, out l1wb, out l2access, out l2hit, out l2ev, out l2wb, out c2c);
//                Console.WriteLine("In MemoryCoalescing, accessed to NoCs (through CmpCache module requester {0}, addr = {1} at cycle {2}", mreq.request.requesterID, mreq.request.address, Simulator.CurrentRound);

                if((winnerClient == currentClient) && (winnerRead == currentRead))
                    consecutiveRequests++;
                if(currentRead != winnerRead)
                    whenSwitchReadWrite = Simulator.CurrentRound;

                mreq.mem_size = ConvertMaskToSize(combineMask);

                uint burstLength = mreq.mem_size / Config.memory.busWidth / 2;
                if(currentRow[mreq.channel_index,mreq.rank_index,mreq.bank_index] == mreq.shift_row) // RB hit
                    whenAvailable[mreq.channel_index,mreq.rank_index,mreq.bank_index] = Simulator.CurrentRound + burstLength;
                else
                    whenAvailable[mreq.channel_index,mreq.rank_index,mreq.bank_index] = Simulator.CurrentRound + Config.memory.cRP + Config.memory.cRCD;
                currentRow[mreq.channel_index,mreq.rank_index,mreq.bank_index] = mreq.shift_row;
                currentClient = winnerClient;
                currentRead = winnerRead;
            }

            int num=0;
            for(int c=0;c<numClients;c++)
            {
                for(int rw=0;rw<2;rw++)
                {
                    num += numInFlightRequests[c,rw];
                }
            }
        }
    }
}
