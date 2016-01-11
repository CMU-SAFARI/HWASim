using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{

    public class ChannelMapping
    {
        public Dictionary<int,int> mapping;
        public Random rand;
        public ChannelMapping()
        {
            mapping = new Dictionary<int,int>();
            rand = new Random();
        }
        virtual public int mappingFunction(int input, ulong numChan, Request req)
        {
        //random for now
            return rand.Next((int)numChan);
        }
        public int getChannel(int input, ulong numChan, Request req)
        {
            int val;
            if(mapping.TryGetValue(input,out val))
                return val;
            else
            {
                int chan = mappingFunction(input, numChan, req);
                mapping.Add(input,chan);
                return chan;
            }
        }
    }
/*
    public class UniformChannelMapping: ChannelMapping
    {
        private ulong[] chanCount = new ulong[Config.Ng];   
        public override int mappingFunction(int input, ulong numChan, Request req)
        {
           ulong min = 999999999;
           int chanMin = 0;
           for(int i=0;i<Config.Ng;i++)
           {
               if(chanCount[i]<min)
               {
                   min = chanCount[i];
                   chanMin = i;
               }
           }
           return chanMin;
        }
    }
    public class StaticChannelMapping: ChannelMapping
    { 
        private static int getChannelFromStaticAssignment(int reqID)
        {
            int[] chanList = chanMappingTable[reqID];
            return chanList[rand.Next((int)chanList.Length)];
        }
        public override int mappingFunction(int input, ulong numChan, Request req)
        {
            return getChannelFromStaticAssignment(req.requesterID);
        }
    }
*/
}
