namespace BatchConvertIsoToXiso.Services.Xiso.Writing;

public static class AvlTree
{
    public static int CompareKey(string lhs, string rhs)
    {
        // Ported from avl_compare_key in extract-xiso.c
        // Custom case-insensitive comparison: 'a'-'z' are treated as 'A'-'Z'
        var i = 0;
        var j = 0;

        while (true)
        {
            var a = i < lhs.Length ? lhs[i++] : '\0';
            var b = j < rhs.Length ? rhs[j++] : '\0';

            if (a is >= 'a' and <= 'z')
            {
                a = (char)(a - 32);
            }

            if (b is >= 'a' and <= 'z')
            {
                b = (char)(b - 32);
            }

            if (a != '\0')
            {
                if (b != '\0')
                {
                    if (a < b) return -1;
                    if (a > b) return 1;
                }
                else
                {
                    return 1;
                }
            }
            else
            {
                return b != '\0' ? -1 : 0;
            }
        }
    }

    public static bool Insert(ref AvlNode? root, AvlNode node)
    {
        if (root == null)
        {
            root = node;
            return true; // Tree height increased
        }

        var cmp = CompareKey(node.FileName, root.FileName);

        return cmp switch
        {
            < 0 when Insert(ref root.Left, node) => LeftGrown(ref root),
            > 0 when Insert(ref root.Right, node) => RightGrown(ref root),
            _ => false
        };
    }

    private static bool LeftGrown(ref AvlNode root)
    {
        switch (root.Skew)
        {
            case AvlSkew.Left:
                if (root.Left is { Skew: AvlSkew.Left })
                {
                    root.Skew = root.Left.Skew = AvlSkew.None;
                    RotateRight(ref root);
                }
                else
                {
                    if (root.Left != null)
                    {
                        if (root.Left.Right != null)
                        {
                            switch (root.Left.Right.Skew)
                            {
                                case AvlSkew.Left:
                                    root.Skew = AvlSkew.Right;
                                    root.Left.Skew = AvlSkew.None;
                                    break;
                                case AvlSkew.Right:
                                    root.Skew = AvlSkew.None;
                                    root.Left.Skew = AvlSkew.Left;
                                    break;
                                default:
                                    root.Skew = AvlSkew.None;
                                    root.Left.Skew = AvlSkew.None;
                                    break;
                            }

                            root.Left.Right.Skew = AvlSkew.None;
                        }

                        RotateLeft(ref root.Left);
                    }

                    RotateRight(ref root);
                }

                return false; // Height didn't increase after rotation

            case AvlSkew.Right:
                root.Skew = AvlSkew.None;
                return false;

            default:
                root.Skew = AvlSkew.Left;
                return true; // Height increased
        }
    }

    private static bool RightGrown(ref AvlNode root)
    {
        switch (root.Skew)
        {
            case AvlSkew.Left:
                root.Skew = AvlSkew.None;
                return false;

            case AvlSkew.Right:
                if (root.Right is { Skew: AvlSkew.Right })
                {
                    root.Skew = root.Right.Skew = AvlSkew.None;
                    RotateLeft(ref root);
                }
                else
                {
                    if (root.Right != null)
                    {
                        if (root.Right.Left != null)
                        {
                            switch (root.Right.Left.Skew)
                            {
                                case AvlSkew.Left:
                                    root.Skew = AvlSkew.None;
                                    root.Right.Skew = AvlSkew.Right;
                                    break;
                                case AvlSkew.Right:
                                    root.Skew = AvlSkew.Left;
                                    root.Right.Skew = AvlSkew.None;
                                    break;
                                default:
                                    root.Skew = AvlSkew.None;
                                    root.Right.Skew = AvlSkew.None;
                                    break;
                            }

                            root.Right.Left.Skew = AvlSkew.None;
                        }

                        RotateRight(ref root.Right);
                    }

                    RotateLeft(ref root);
                }

                return false;

            default:
                root.Skew = AvlSkew.Right;
                return true;
        }
    }

    private static void RotateLeft(ref AvlNode root)
    {
        var temp = root;
        if (root.Right != null)
        {
            root = root.Right;
        }

        temp.Right = root.Left;
        root.Left = temp;
    }

    private static void RotateRight(ref AvlNode root)
    {
        var temp = root;
        if (root.Left != null)
        {
            root = root.Left;
        }

        temp.Left = root.Right;
        root.Right = temp;
    }
}
