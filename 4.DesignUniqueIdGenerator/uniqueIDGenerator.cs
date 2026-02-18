using System;

public class SequenceGenerator
{
    private static int NODE_ID_BITS = 10;
    private static int SEQUENCE_BITS = 12;
    private static int maxNodeId = (int)(Math.Pow(2, NODE_ID_BITS) - 1);
    private static int maxSequence = (int)(Math.Pow(2, SEQUENCE_BITS) - 1);
    // Custom Epoch (January 1, 2015 Midnight UTC = 2015-01-01T00:00:00Z)
    private static long CUSTOM_EPOCH = 1420070400000L;
    private int nodeId;
    private long lastTimestamp = -1L;
    private long sequence = 0L;

    // Create SequenceGenerator with a nodeId
    public SequenceGenerator(int nodeId)
    {
        if (nodeId < 0 || nodeId > maxNodeId)
        {
            throw new Exception("NodeId must be between {{0}} and {{maxNodeId}}");
        }
        this.nodeId = nodeId;
    }

    public long nextId()
    {
        long currentTimestamp = timestamp();

        if (currentTimestamp < lastTimestamp)
        {
            throw new Exception("Invalid System Clock!");
        }
        if (currentTimestamp == lastTimestamp)
        {
            sequence = (sequence + 1) & maxSequence;
            if (sequence == 0)
            {
                // Sequence Exhausted, wait till next millisecond.
                currentTimestamp = waitNextMillis(currentTimestamp);
            }
        }
        else
        {
            // reset sequence to start with zero for the next millisecond
            sequence = 0;
        }
        lastTimestamp = currentTimestamp;
        long id = currentTimestamp << (NODE_ID_BITS + SEQUENCE_BITS);
        id |= (nodeId << SEQUENCE_BITS);
        id |= sequence;
        return id;
    }
    private static long timestamp()
    {
        DateTimeOffset t = new DateTimeOffset();
        long secondsSinceEpoch = t.ToUnixTimeMilliseconds();
        return Math.Abs(secondsSinceEpoch - CUSTOM_EPOCH);
    }
    // Block and wait till next millisecond
    private long waitNextMillis(long currentTimestamp)
    {
        while (currentTimestamp == lastTimestamp)
        {
            currentTimestamp = timestamp();
        }
        return currentTimestamp;
    }
}
public class Program
{
    static public void Main()
    {
        SequenceGenerator obj = new SequenceGenerator(786); //Pass the machineId
        Console.WriteLine(obj.nextId());
        Console.WriteLine();
    }
}
/*
*    Let’s now understand how it works. Let’s say it’s June 9, 2018 10:00:00 AM GMT. 
*    The epoch timestamp for this particular time is 1528538400000.
*
*    First of all, we adjust our timestamp with respect to the custom epoch-
*
*    currentTimestamp = 1528538400000 - 1420070400000 // 108468000000 (Adjust for custom epoch)
*    Now, the first 41 bits of the ID (after the signed bit) will be filled with the epoch timestamp. 
*    Let’s do that using a left-shift -
*
*    id = currentTimestamp << (10 + 12)
*    Next, we take the configured node ID and fill the next 10 bits with the node ID. Let’s say that the nodeId is 786 -
*
*    id |= nodeId << 12
*    Finally, we fill the last 12 bits with the local counter. Considering the counter’s next value is 3450, 
*    i.e. sequence = 3450, the final ID is obtained like so -
*
*    id |= sequence  // 454947766275219456
*    That gives us our final ID.
*
*/